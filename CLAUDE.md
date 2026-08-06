# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

SessyWeb is a home energy management system (HEMS) for households with [Sessy](https://www.sessy.nl) home batteries. It runs as a Docker container on a NAS and uses a MILP (Google OR-Tools) to plan battery charge/discharge over a 72-hour horizon against day-ahead EPEX prices, solar forecast and consumption forecast. Everything is local — SQLite, no cloud beyond the ENTSO-E and WeerLive API calls.

Target framework is **net10.0** across all projects (the README's ".NET 9" and the Dockerfile comments are stale; the Dockerfile itself uses `mcr.microsoft.com/dotnet/aspnet:10.0`).

## Commands

```powershell
# Build everything
dotnet build C:\Projects\Sessy\SessyController.sln

# Run the app (SessyWeb is the only executable project; SessyController is OutputType=Library)
dotnet run --project SessyWeb\SessyWeb.csproj

# All tests
dotnet test SessyUnitTests\SessyUnitTests.csproj

# Single test class / single test (xunit v3)
dotnet test SessyUnitTests\SessyUnitTests.csproj --filter "FullyQualifiedName~EnergySystemStateMachineTests"
dotnet test SessyUnitTests\SessyUnitTests.csproj --filter "FullyQualifiedName~EnergySystemStateMachineTests.MethodName"
```

### EF Core migrations

`ModelContext` lives in **SessyData**, the startup project is **SessyWeb**, and there is no `IDesignTimeDbContextFactory`, so both flags are required. **Always `dotnet build` first** — `dotnet ef` reads the compiled assembly, so an unbuilt model change silently produces an empty or wrong migration.

```powershell
dotnet build C:\Projects\Sessy\SessyController.sln
dotnet ef migrations add <Name> --project SessyData --startup-project SessyWeb
```

Migrations are applied automatically at startup in `SessyWeb/Program.cs` (`dbContext.Database.Migrate()`), preceded by an automatic `VACUUM INTO` backup when pending migrations exist.

## Repository conventions

- **Do not run git operations** (commit, push, branch) — local edits only unless explicitly asked.
- **Bump the version on every change**: increment the patch in `SessyCommon/AppInfo.cs` (`public const string Version = "v1.0.x";`). MainLayout shows it and `Program.cs` writes it into the `AppVersions` table at startup, so this is the only place to edit.
- Comments and log messages are a mix of English and Dutch; match the surrounding file.
- Region separators use the `// ── Name ─────` box-drawing style.

## Project layout

| Project | Role |
|---|---|
| `SessyWeb` | Blazor Server UI (Radzen), `Program.cs` (all DI wiring), API controllers, EF migrations run here |
| `SessyController` | Domain + background services: MILP planner, hardware polling, state machine, inverter drivers |
| `SessyData` | EF Core / SQLite: `ModelContext`, entity models, one `*DataService` per entity |
| `SessyCommon` | Config POCOs, extensions, `TimeZoneService`, `ServiceLocator`, `DockerService` |
| `Djohnnie.SolarEdge.ModBus.TCP` | Vendored SolarEdge Modbus library |
| `SessyUnitTests` | xunit v3 + Moq |

## Architecture

### Startup and DI (SessyWeb/Program.cs)

Every service is registered here — this file is the map of the system. Key points:

- Config is loaded from `$CONFIG_PATH/appsettings.json` (+ optional `secrets.json`), **not** the project's own appsettings. `CONFIG_PATH` defaults to the working directory. Sections bind to `SessyBatteryConfig`, `SessyP1Config`, `PowerSystemsConfig`, `SettingsConfig`, `WeatherExpectancyConfig`, `SolarEdgeCloudConfig`, `HeatPumpConfig`.
- **`SettingsService` must remain the first `AddHostedService`.** All other background services `await _settingsService.WaitForReadyAsync()` before their first cycle.
- Most services are singletons registered *both* as themselves and as their interface via a `sp => sp.GetRequiredService<T>()` factory — never add a second `AddSingleton<IFoo, Foo>()` alongside, that creates a duplicate instance.
- `ServiceLocator.ServiceProvider` is set post-build for the few places that resolve outside DI.

### Settings: two separate config systems

1. **`appsettings.json`** — infrastructure only: timezone, connection string, backup dir, battery/meter/inverter IPs. Read via `IOptionsMonitor<T>`; changes fire `OnChange`.
2. **The `Settings` DB row** — all operational tuning (strategy, cycle cost, reserve %, efficiency, consumption profile, manual override). Owned by `SettingsService`; read via `_settingsService.Current`; after the UI saves, call `SettingsService.RefreshAsync()` which fires `SettingsChanged(Settings, bool isStartup)`.

Services cache `Settings` in a field and refresh it in the `SettingsChanged` handler. When adding a setting that must trigger a plan rebuild, set a rebuild reason in that handler (see `MilpServiceBase._configChangedReason`).

### Planning pipeline

`BatteriesService` (the main loop, a `BackgroundHeartbeatService`, 60s / 10s in DEBUG) drives each cycle:

1. Gather `List<QuarterlyInfo>` — one object per quarter-hour holding price, solar, consumption, mode and SOC. `QuarterlyInfo` is the central data structure passed between the planner, the UI and the DB writers; measured quarters are constructed from a `QuarterlyMeasurement` (`IsMeasured = true`), future quarters via `CreateAsync`.
2. `IMilpService.BuildPlanAsync(quarterlyInfos, currentSocWh)` — rebuilds only when needed (price change, SOC deviation > 20%, settings change).
3. `EnergySystemStateMachine.Evaluate(EnergySystemInput)` decides the actual mode.
4. Execute one action per quarter via the Sessy Open API (`SessyService` / `BatteryContainer` / `Battery`).

**Strategy selection**: `IMilpService` is registered as `MilpServiceProxy`, which dispatches per call to `ProfitMaximizationMilpService`, `SelfConsumptionMilpService`, `BalancedMilpService` or `BatterySavingMilpService` based on `Settings.Strategy` — so a strategy change in the UI takes effect without a restart. All four derive from `MilpServiceBase` (~1000 lines: OR-Tools model, SOC bookkeeping, throttle ratios, plan persistence); the per-strategy deltas live in `Services/Optimization/Strategies/`. Put shared solver changes in `MilpServiceBase`, objective/constraint differences in the strategy.

### State machine

`EnergySystemStateMachine` is the single place where battery mode and inverter setpoint are decided — "all transition logic lives here, nowhere else". Curtailment (negative selling price) overrides the MILP plan; modes are `ZeroExport`, `Throttle`, `Shutdown`, `None`. `InverterCurtailmentService` polls `CurrentAction` every 5s. It has no DI dependencies beyond a logger, which is why it is the most heavily unit-tested class — extend `EnergySystemStateMachineTests`' input matrix when adding a transition.

### Data access

`DataService` classes derive from `ServiceBase<T>` (`Add`, `AddOrUpdate`, `Update`, `Remove`, `Get`, `GetList`, `Query`, `RemoveWhere`) and go through `DbHelper`, dat per aanroep een verse scope + `ModelContext` maakt. Sinds v1.0.40:

- **Schrijven** loopt door één **statische** `SemaphoreSlim` — proces-breed, want SQLite laat maar één schrijver toe. (Vóór v1.0.40 stond hier "één proces-brede semafoor voor álle toegang"; dat klopte niet: `DbHelper` is `AddScoped` en `ServiceBase` maakt een eigen scope, dus het slot gold per data-service. Het enige echt globale slot zat in SQLite zelf.)
- **Lezen** loopt via `ExecuteQueryAsync` (max 4 tegelijk per data-service) en blokkeert geen thread meer. De oude synchrone `ExecuteQuery` met `Wait()` parkeerde een thread-pool thread per query — de directe oorzaak van seconden-lange GUI-haperingen.
- Nooit een `ModelContext` over aanroepen heen vasthouden; nooit een data-service aanroepen *binnen* de callback van een andere schrijfactie (deadlock op het statische schrijfslot).
- Reads use `AsNoTracking()`, so returned entities are detached — write back through `AddOrUpdate`/`Update`.
- `AddOrUpdate`/`Update`/`Remove` require `T : IUpdatable<T>` and use that `Update()` implementation to copy fields.
- `ExecuteTransaction` throws if `SaveChangesAsync` writes 0 rows. `RemoveWhere` (bulk delete) draait daarom via `ExecuteWriteAsync` zónder transactie — `ExecuteDelete` gaat buiten de change-tracker om en zou anders teruggerold worden.
- Grote upserts: geef `ServiceBase.MatchOn(key, window)` mee aan `AddOrUpdate` in plaats van een `contains`-lambda die per rij een query doet.
- Verbindingen krijgen `journal_mode=WAL`, `synchronous=NORMAL` en `busy_timeout=5000` via `SqlitePragmaInterceptor`.

### Background services

Long-running services derive from `BackgroundHeartbeatService` (a `BackgroundService` with an `OnHeartBeat` event so the UI can trigger an immediate cycle). Since they are singletons and DB services are scoped-per-call, they create one `IServiceScope` in the constructor and resolve from it — follow that pattern rather than injecting scoped services directly.

### Time

Never use `DateTime.Now`. Use `TimeZoneService.Now` (configured timezone, `virtual` so tests can mock it) and the `DateTimeExtension` helpers (`DateFloorQuarter()` etc.). Quarter-hour alignment is assumed everywhere in the planner.

### UI

Blazor Server, Radzen components, `.razor` + `.razor.cs` code-behind pairs. Pages inherit `PageBase`, components inherit `BaseComponent` — both inject `BatteryContainer`/`BatteriesService` and expose `IsManualOverride`, `WeAreInControl`, `ChargedInControl`, a cascading `ScreenInfo` (mobile/landscape detection via BlazorSize) and `GetFormatProvider()` (hardcoded `nl-NL`). `PageBase` additionally cascades `SetIsBusy` for the global spinner and sets `HideId` from the DEBUG flag. Swagger is exposed at `/swagger`.

## Deployment

`SessyWeb/Dockerfile` builds and publishes SessyWeb. Container expects volumes at `/SessyController/Config` (appsettings.json) and `/SessyController/Data` (SQLite DB + backups), ports 80/443, and `CONFIG_PATH=/SessyController/Config`. See README.md for the full Synology Container Manager setup.

# SessyWeb — Samenvatting voor nieuwe chat

## Wat is SessyWeb
C#/.NET Blazor Server EMS. Stuurt 3× Sessy batterij (cap 16,2 kWh; raw charge 6600W/discharge 5100W; **aantoonbaar gehaald ~5,0-5,3 kW laden** over vrijwel het hele SOC-bereik — zie Openstaande punten, de eerdere "praktijk max ~4,4kW" was de getaperde planwaarde, niet de hardwarelimiet), SolarEdge inverter, Daikin warmtepomp. Draait op Synology NAS via Docker. Huidige versie **v1.0.56**. Locatie: Apeldoorn.

## Werkafspraken
- Antwoorden in het Nederlands, caveman-ultra kort. Code-commentaar in het Engels, **kort — één regel waar mogelijk** (user leest alle comments na ter controle).
- **Niet gissen**: eerst verifiëren in code/DB/web. Deze sessie ging herhaaldelijk mis door aannames — meerdere keren "gevonden!" geroepen en ernaast gezeten. Bij planner-analyse: DB-tabellen naast elkaar leggen en met de Python-simulator narekenen vóór conclusies.
- Code-behind boven `@code`-blokken. Radzen Blazor overal. Radzen API's verifiëren via `raw.githubusercontent.com/radzenhq/radzen-blazor/master/...` (rendering-pad én crosshair/tooltip-pad — `CartesianSeries.DataAt/TooltipY` unwrapt `double?` hard).
- Na codegeneratie altijd zelf reviewen op syntax/naamfouten/dubbele code/missing usings. Braces + code-parens balanceren (comments negeren bij paren-telling).
- **Versie ophogen bij ELKE wijziging** in `SessyCommon/AppInfo.cs`: `public const string Version = "v1.0.39";` (enige plek sinds v1.0.39; MainLayout leest hem, Program.cs schrijft hem in de tabel `AppVersions`). Patch-level ophogen (1.0.39 → 1.0.40).
- Output: volledige bestanden naar `/mnt/user-data/outputs/`, delen via `present_files`.

## Omgeving
- Broncode: `/tmp/prod/SessyWeb-master/`. DB-uploads → `/tmp/prod_db.db`, geanalyseerd met Python `apsw` (`pip install --break-system-packages`). Geen dotnet in container.
- Simulators: `/tmp/sim_planner3.py` spiegelt `BatteryGreedyPlanner` (continue discount + stock-cost). **Kritiek**: sim en C# moeten identieke loop-grenzen houden — een `j=1` vs `range(0,n)` discrepantie liet tests ten onrechte "opgelost" melden.

## Planner-architectuur (BatteryGreedyPlanner.cs)
Deterministisch, greedy, twee passes: (1) ZeroNetHome-baseline, (2) arbitrage in 0,1 kWh-blokken. Candidate A = ontladen van al-aanwezige energie (geen gekoppeld laadmoment); Candidate B = laden-i → ontladen-j met `i<j`.
- **FutureValueDiscountPerHour** (default 0,003 — UI toont procenten, dus 0,3): continue korting op waarde[j]/kosten[i], vervangt de oude discrete "near-term hedge" die drie keer op randgevallen stukliep. Rapportage/objective gebruiken echte prijzen. Default afgeleid uit gemeten planstabiliteit in `PlannedActions` (94-97% tot 18 u lead → 0,002-0,006/u). Hoort samen met `PredictedPriceMode`: bij `Off` is >24 u toch `reserveOnly`; zet je die op `SoftMargin`, dan moet de discount omhoog want daar houdt maar 23-63% stand.
- **StockCostEurPerKWh**: FIFO-gemiddelde als bodemprijs (reservatieprijs) voor Candidate A — voorkomt dumpen van doorschuif-energie. Zon-voorraad = 0.

## Grote bugs deze sessie opgelost
1. **`j=1` → `j=0`** (BatteryGreedyPlanner): arbitrage-loop sloeg index 0 (= huidig kwartier) over. Sinds horizon bij `nowQuarter` begint, schoof elke ontlading eeuwig één kwartier vooruit → nooit uitgevoerd. Kernbug.
2. **Speculative-solve rollback** (MilpServiceBase v1.0.14): `ApplySolveResult` installeerde plan vóór accept/reject; een afgewezen solve bleef de batterij aansturen zonder spoor in DB. Nu snapshot `_planByTime`/`_planSocWhByTime` vóór solve + herstel + `WriteBackSocSimulationAsync` bij reject.
3. **SOC-deviatie meetfout** (v1.0.13→1.0.16): vergeleek live mid-kwartier SOC met END-of-quarter plan-SOC → spookafwijking ~7-13% tijdens (dis)charge → onnodige forced replans. v1.0.13 interpoleerde maar was gebroken (horizon start bij nowQuarter → geen voorganger, altijd fallback). v1.0.16 fix: `_planStartSocWh`/`_planStartQuarter` bijgehouden in ApplySolveResult (let op: param `socKWh` is kWh, ×1000 → Wh), meegenomen in rollback-snapshot en ClearPlanAsync.
4. **Guards weigerden i.p.v. clampen** (v1.0.7): laad-guard gebruikte vol vermogen niet gepland; ontlaad-guard had hardcoded `+50Wh`. Bovenste ~1,6 en onderste ~0,5 kWh onbereikbaar. Nu clampen naar beschikbare ruimte/energie, const `MinimumUsefulWh=25`. Alle NZH/Disabled-takken loggen op Warning (prod log-level = Warning).
5. **Sticky mutations** verwijderd: guards schreven downgrades permanent in `_planByTime` → één ruizige meting bleef het hele kwartier plakken.

## Revenue/FIFO-correcties (v1.0.11-1.0.12)
Laadkant waardeerde overal álle lading tegen inkoopprijs, ook gratis zon → verzonnen negatieve "revenue"-labels. Gefixt: `QuarterlyInfo.Profit` (beide kanten gesplitst via NetLoadWh), `MeasurementView.GridChargeCostEur` (= `min(GridImport,Charged)×buy`, spiegelt DischargeValueEur), `EnergyStatisticsService` twee plekken op één conventie, dode `CalculateAveragePriceOfChargeInBatteries` (65 regels) verwijderd. `Profit` wordt als `QuarterlyMeasurement.PlannedRevenueEur` in DB geschreven — historische rijen houden oude waarden (user koos niet-herberekenen).

## Overige features
- **Charged handoff** (v1.0.6): bij `ChargedInControl` → Sessy native strategie (ProfitMax→ROI, SelfConsumption→ECO, anders ROI). `Battery.SetActivePowerStrategyToRoi/Eco`, `BatteryContainer.StartRoi/Eco`. Binnen `#if !DEBUG`.
- **Plan history overlay** in ChargingHoursChartComponent: valt samen met "Show all", dropdown toont plannen binnen hetzelfde `[from,to)`-venster. Solide magenta lijn, bovenste serie.
- **Actual power series**: areas in plan-kleuren, gebonden aan gefilterde `ActualPowerPoints` (`HasActualPower`) zodat ze bij "nu" stoppen i.p.v. plan dubbel te tekenen.
- **Last-plan reason** zichtbaar in header onder SOC dev.
- **Curtailment unit tests** (`CurtailmentPricingTests.cs`): netting on/off × pos/neg marktprijs × belasting/vergoeding/BTW.
- Obsolete "Shut down at negative prices" verwijderd (superseded door InverterCurtailmentService).

## Zelf-lerende planner (interview afgerond, NOG NIET gebouwd)
User's doel: investering z.s.m. terugverdienen, winst leidend. Terugvalpunt-spec: open horizon (geen harde cap), minimale reserve, zelf-lerende discount-rate én nacht-reserve uit 21-daagse voorspelfouten (zon+verbruik samen), nachtelijk herberekend, bounds ~0,5-3%/uur (ondergrens boven 0% houden), alert bij grens-pinning, log van geleerde waarde, geleerd overschrijft handmatig. Fallback 1% tot 21 dagen data. Op bestaande Settings-pagina.

## Opgelost 27-07 (v1.0.19 / v1.0.20)
1. **`PlannedUnthrottledPowerW` = 0 in alle rijen — OPGELOST (v1.0.19).** `WriteBackSocSimulationAsync` en de twee clamp-takken in `GetExecutableActionAsync` riepen `SetPlanPower` met 2 args aan → default unthrottled=0 overschreef wat `WritePlanIntoQuarterlyInfos` net had gezet. Alle vier callsites geven nu `Unthrottle(...)` mee. Historische rijen blijven 0; vult zich vanaf de eerstvolgende rebuild.
2. **Laadtaper genegeerd door de planner — OPGELOST (v1.0.20).** `ThrottleAnalysisService` bucketde alleen op buitentemperatuur; nagerekend op de productie-DB is dat signaal vlak (0,74-0,79 over 18-24°C) terwijl SOC monotoon daalt (1,00 bij 20% → 0,65 bij 80%). Gevolg 27-07: gepland 15319 Wh laden in 13 kwartieren, geleverd 11536 Wh (-25%), eindstand 87,7% terwijl er 23 even goedkope ZeroNetHome-kwartieren ongebruikt bleven. Nieuw: `ChargeTaper` (least-squares fit `ratio = A - B*soc`, min 20 samples, productie-fit A=1,08 B=0,585 op 196 samples) in `ThrottleAnalysisService.GetChargeTaperAsync()`, doorgegeven via `BatterySpec.ChargeTaper` en per kwartier toegepast in `BatteryGreedyPlanner` op basis van de SOC aan het begin van dat kwartier. Bij een geldige taper wordt de temperatuur-charge-ratio op 1,0 gezet (anders dubbel derated); ontladen houdt de temperatuurbuckets. `Unthrottle()` deelt op de laadkant nu door de taper.

3. **Throttle-model op SOC + temperatuur + warmtestuwing (v1.0.21).** `ratio = A - B*soc - C*(temp-20) - D*(t48-temp)`, OLS op drie regressors in `ThrottleAnalysisService.GetChargeTaperAsync()`. Productiefit (312 samples): A=1,054 B=0,535 C=0,0237 D=0,0152; R² 0,316 (SOC) → 0,477 (+temp) → 0,508 (+stuwing, t=−4,4). Herschreven `−0,0085*temp − 0,0152*t48`: het 48-uursgemiddelde weegt bijna dubbel. `PricePoint` draagt `TemperatureC`/`Temperature48hC`; t48 voor toekomstige kwartieren = gemeten historie (`Consumption.Temperature`) + forecast (`WeatherService`). Fallbackketen: 3 regressors → SOC-only fit → `None`. Let op bij analyse: enkelvoudige correlaties zijn hier misleidend (temp en t48 correleren onderling sterk) — altijd multivariate fit.

4. **Twee tijdschalen + veroudering (v1.0.22).** C en D (huisgedrag) worden gefit over een lange window; A en B (batterij-taper, drift door degradatie/firmware) over de recente 31 dagen mét C/D vastgezet, zodat een hittegolf niet als batterijslijtage wordt gelezen. Tegen een structurele wijziging (airco in de technische ruimte, isolatie) staan twee remmen: harde cap `TaperHistoryDays = 730` en exponentiële veroudering `TaperHalfLifeDays = 120` (30d = 84%, 120d = 50%, 365d = 12%, 730d = 1,5%). Gewogen kleinste kwadraten via de optionele `weights`-parameter op `SolveLeastSquares`. Fit gecached (`TaperCacheTime` 6u) en t48 via prefix-sums + binary search — zonder dat wordt de fit kwadratisch in de historielengte. Gewogen vs ongewogen op de huidige data: A=1,057 B=0,543 C=0,0231 D=0,0149 tegen A=1,054 B=0,535 C=0,0237 D=0,0152 (nauwelijks verschil, want alle data is nu recent).

## Gebouwd 30-07 (v1.0.30)
1. **Terminal value / replacement cost.** `ReplacementCostService`: laag percentiel (default P25) van de dagelijks goedkoopste all-in inkoopprijs over een sleepvenster (default 30 dagen), afgetopt op de mediaan-inkoopprijs van datzelfde venster. Vervangt `StockCostEurPerKWh` als `SessyOptions.ReplacementCostEurPerKWh`; de FIFO-kostenbasis is nog slechts terugval zolang er te weinig prijshistorie is. Symmetrisch gebruikt: (a) bodem onder het verkopen van voorraad — nu gedeeld door de round trip, de oude code vergeleek een AC-prijs met een geleverde kWh; (b) nieuwe **Candidate C** in `BatteryGreedyPlanner` die puur laadt om energie voorbij de horizon vast te houden. Candidate C draagt géén cycluskosten (die vallen tegen elkaar weg — de kWh wordt één keer gecycled, welke dag je ook laadt) en discontéért de waarde op het einde van de horizon, niet op het laadkwartier. Eind-SOC telt mee in de objective, anders scoort elk carry-forward-blok als verlies. Achter `Settings.CarryForwardEnabled`, **default uit**.
2. **Zelf-lerende planner-parameters.** Nieuwe tabel `ForecastSnapshots` (Time, LeadHours-ladder 0/1/2/3/4/6/8/12/16/20/24/30/36/48, solar- en verbruiksforecast) — één keer per (kwartier, bucket) geschreven vanuit `MilpServiceBase`, nooit ge-upsert, want `PlannedQuarter` overschrijft zijn forecast elke rebuild en is daardoor waardeloos voor foutmeting op afstand. Retentie 40 dagen. `PlannerLearningService` draait één keer per nacht (na 03:00, aangeroepen vanuit de BatteriesService-lus) en fit over 21 dagen: discount uit de gecombineerde netto-belastingfout per lead-bucket via `r(h) = e(h) / (h·(1−e(h)))`, mediaan over de buckets, minus de fout die al op lead 0 zit; nachtreserve uit P80 van wat het huis werkelijk trok tussen 21:00 en 07:00. Grenzen 0,5–3 %/uur en 5–60 %; pinnen logt Warning én verschijnt in Tips & Checks. Terugval 1 %/uur tot 21 dagen data. Geleerde waarden overschrijven de handmatige (die velden zijn read-only in de UI zolang het aan staat). Achter `Settings.SelfLearningEnabled`, **default uit**.
3. Tests: `CarryForwardPlannerTests` (9) en `PlannerLearningTests` (12). De discount-fit is inverteerbaar getest — een reeks gegenereerd uit een bekende r moet exact die r teruggeven.

## Gebouwd 04-08 (v1.0.37)
**Toekomstige kostenbasis + twee FIFO-fouten opgelost.** `ChargeCostBasisService` is nu de enige
FIFO-motor, met twee voedingen: gemeten historie en het lopende plan (`ProjectAsync`, met een pure
`Project(...)` eronder zonder klok of DB). De projectie zat eerder los in
`MilpServiceBase.ProjectCostBasisAsync`; die krimpt tot een mapping.
1. **Gratis-deel kwam uit totale zonproductie** — laden uit het net tijdens een zonnig kwartier
   waar het huis alle zon opat werd als gratis geboekt, in beide takken. Nu: gemeten kant
   `min(GridImport, Charged)` uit de P1-meterdelta's (`SplitChargeByMeter`, dezelfde conventie als
   `MeasurementView.GridChargeCostEur`), plangekant `max(0, -NetLoadWh)` (`SplitChargeBySurplus`).
   `LoadSolarAsync` is vervangen door `LoadGridImportAsync`.
2. **Rendement ontbrak.** Lagen dragen nu opgeslagen (DC) Wh tegen `buy/chEff` per opgeslagen kWh;
   ontladen drukt `wh/disEff` uit de stapel. Geleverde kostenbasis = opgeslagen / disEff, en dat is
   het getal dat náást de verkoopprijs hoort — de grafiekserie is daarop omgezet
   ("Cost basis delivered"), de tooltip toont beide. Was ~15% te optimistisch.
3. Nieuw in de UI (`CostBasisComponent` + `CostBasisLayerGridComponent`): blok "Projected — end of
   plan horizon" met eind-lagen, kostenbasis opgeslagen/geleverd en wat het plan aan netinkoop van
   plan is (kWh, €, gemiddelde €/kWh). Gevoed uit `ChargeCostBasisService.LastProjection`.
4. `PlannedQuarter.ProjectedCostBasisEurKWh` (migratie `AddProjectedCostBasisToPlannedQuarter`)
   bewaart de projectie per kwartier, zodat geprojecteerd vs werkelijk achteraf meetbaar wordt.
5. Bijvangst: `RemoveOldest` liet stoflaagjes van ~1e-13 Wh achter die nooit meer leegliepen en het
   gemiddelde vervuilden. Alles onder `MinLayerWh` gaat er nu in zijn geheel uit.
Geen datareparatie nodig — de FIFO wordt bij elke start uit metingen herbouwd. Tests:
`ChargeCostBasisTests` (14).

## Gebouwd 06-08 (v1.0.38)
**Taper losgekoppeld van het gevraagde laadvermogen — zelfversterkende meetfout.** De batterijen
kregen als setpoint `PlannedPowerW`, het al door de taper verlaagde getal, terwijl
`ThrottleAnalysisService` de ratio mat tegen `PlannedUnthrottledPowerW`. Zodra de hardware het
verlaagde verzoek haalde (actual/planned = 1,00) mat de fit zijn eigen verzoek: 28-07 vroeg 76% van
nameplate en zat écht tegen de limiet (0,77), vanaf 30-07 vroeg de planner 53-59% en leverde de
hardware daar 100% van. `Math.Min(ratio, 1.0)` knipte alleen naar boven af, dus de drift was
eenrichtingsverkeer. Gevolg: laden op 2,5-3,5 kW terwijl 4,4-5 kW aantoonbaar haalbaar was
(06-08 11:30 gevraagd 4240 → geleverd 4270 bij SOC 30%), goedkoop venster uitgesmeerd over 5 uur,
eind-SOC 86%.
1. **Planner plant twee getallen.** `PlanStep.RequestedChargeKW` naast `ChargeKW`: de taper bepaalt
   nog steeds hoeveel energie er aankomt (SOC-pad, objective, sessieduur), maar waar de taper de
   bindende cap was is het verzoek de nameplate. Waar de greedy-allocatie uit zichzelf onder de cap
   stopt is verzoek = allocatie. De reconstructie `Unthrottle(planW / taperRatio)` is daarmee weg op
   de laadkant (ontladen houdt `_throttleRatioByTime`); `_chargeThrottleRatioByTime` was daarna
   dood en is verwijderd.
2. **Executie stuurt het verzoek.** `GetExecutableActionAsync` klemt alleen nog op de ruimte en op
   `SessionRemainingWh` — plan-SOC aan het einde van de laatste aaneengesloten laadkwartier minus de
   live SOC. Voorkant van een sessie mag vol vermogen (energie naar voren schuiven binnen één
   prijsblok is prijsneutraal), de staart koopt niet extra in. Nieuwe log `GUARD_CHARGE_TARGET_REACHED`.
3. **Zon-guard repareerd.** `GUARD_CHARGE_SOLAR_SURPLUS` toetste `NetLoadWh < -chargeStepWh`
   (−1650 Wh) terwijl het dak op 914 Wh/kwartier piekt: kon nooit afgaan. Vervangen door een bodem
   in `ChargeSetpointW`: setpoint = `max(verzoek, zonoverschot)`, geklemd op wat er nog past.
4. **Envelope-fit.** `SelectEnvelope`: per SOC-bin (10 bins) alleen wat binnen 0,10 van de beste
   ratio ligt, minimaal 3 punten. Een bandbreedte, geen percentage — bij "de bovenste kwart" laten
   nieuwe lage samples de fit alsnog zakken. Knip op 1,0 vervangen door samples > 1,02 weggooien.
   `MinTaperSamples` (60) → `MinEnvelopeSamples` (30), want envelope-punten zijn minder ruizig.
   Nagerekend op de productie-DB verandert de fit nu nauwelijks (A=0,79 B=0,60 tegen A=0,83 B=0,65):
   *alle* historische samples zijn vervuild, de envelope kan pas herstellen zodra er eerlijke
   samples binnenkomen. Dat gaat snel: nieuwe samples liggen hoger, dus de band laat de oude vallen.
Tests: `ChargeRequestTests` (11).

**Versie in de DB (v1.0.39).** De versie stond als literal in `MainLayout.razor` — zichtbaar, maar
door niets anders leesbaar. Nu `SessyCommon/AppInfo.cs` als enige bron; `Program.cs` schrijft hem
direct na `Database.Migrate()` in de nieuwe tabel `AppVersions` (migratie `AddAppVersion`): één rij
per versie met `FirstSeen` (`[SkipCopy]`, wordt nooit overschreven), `LastSeen` en de laatst
toegepaste migratie. Wijkt de vorige rij af, dan logt de start
"Database last ran under vX, now vY" — zo zie je ook een oudere build op een nieuwere DB. Tests:
`AppVersionRecordingTests` (3, tegen een echt SQLite-bestand met de echte migraties).

## Gebouwd 06-08 (v1.0.40)
**GUI-haperingen: klikken kwamen soms seconden later aan, op élke pagina.** Vier oorzaken, van
groot naar klein:
1. **`DbHelper.ExecuteQuery` blokkeerde threads.** Eén overload, beginnend met een synchrone
   `Wait()`, en álle leesacties (`ServiceBase.Get/GetList/Exists`) liepen erlangs. Elke query
   parkeerde een thread-pool thread; de pool groeit met ~1 thread/seconde, dus onder belasting
   kwamen ook de SignalR-berichten van de klikken seconden te laat. Bovendien gaf die overload bij
   een async-lambda de Task terug ná het disposen van de scope — dat ging alleen goed omdat de
   SQLite-provider vrijwel alles synchroon afhandelt. Nu `ExecuteQueryAsync` (await binnen de
   scope) plus `ExecuteWriteAsync` voor statements die geen transactie verdragen.
2. **Geen WAL.** De DB stond op `journal_mode=delete` + `synchronous=FULL`: een schrijver
   vergrendelt het hele bestand en doet twee fsyncs per commit, dus élke lezer wachtte. Dit was het
   enige écht proces-brede slot (de "process-wide semafoor" uit de oude documentatie bestond niet —
   `DbHelper` is `AddScoped`). Nu WAL + `synchronous=NORMAL` + `busy_timeout`; schrijven zit achter
   één statisch slot. **Let op waar je `journal_mode` zet (v1.0.41):** het staat ín het
   databasebestand, dus het is een schrijfactie die exclusieve toegang vraagt. Vanuit
   `SqlitePragmaInterceptor` op élke connectie-open gaf dat "SQLite Error 8: attempt to write a
   readonly database" op de eerste connectie, nog vóór de migraties. Nu één keer bij het starten via
   `SqliteSetup.EnableWriteAheadLogging` (Program.cs, vóór `GetPendingMigrations`), met een log-regel
   in plaats van een crash als het niet lukt; de interceptor doet alleen nog sessie-pragma's die
   niets schrijven.
3. **Tabelscans en tabellen in het geheugen.** `PlannedQuarters`, `ActualQuarters` en
   `PlannedActions` hadden geen index op `Time` (migratie `AddPlanTimeIndexes`), de plan-upsert deed
   ~288 losse lookups binnen één schrijftransactie (nu `MatchOn`), en `GetPlanHistoryAsync` /
   `LoadPlanAsync` trokken alle 79.776 `PlannedActions`-rijen in het geheugen — bij elke
   dashboard-refresh, per open tab. Nu groepeert en filtert SQL dat; purges gaan via één DELETE.
4. **Per tab werd de hardware gepolld en de hele pagina hertekend.** `Battery.GetPowerStatus` en
   `GetActivePowerStrategy` cachen nu 2 s met single-flight, dus de belasting schaalt met tijd in
   plaats van met het aantal tabs; retry ging van 10×5 s naar 3×1 s en `HttpClient` kreeg een
   timeout van 5 s (was de default van 100 s). De spinner zit in een eigen `BusyOverlay`-component
   (stond in `MainLayout`, dus elke flip hertekende `@Body` inclusief de grafiek), de grafiek heeft
   `ShouldRender` op basis van een echte wijziging, en de 5-seconden-loop hertekent alleen nog bij
   een gewijzigde status.

Meetpunten die blijven staan: `DbHelper` logt trage wachttijden/houdtijden, `ThreadPoolMonitorService`
meldt een oplopende werkqueue, `RenderTimer` logt "Slow render: <component> took N ms", en `Clock`
logt "UI blocked: clock tick ran N ms late" (één regel per blok — de timer tikt door terwijl de
dispatcher vastzit, dus een stall loopt leeg als een aflopende reeks). Tests:
`DbHelperConcurrencyTests` (5, tegen een echt SQLite-bestand incl. WAL-check).

**Vervolg (v1.0.43).** Eerste productie-log gaf precies waar de instrumenten voor bedoeld waren:
géén `DbHelper: slow` en géén `ThreadPool busy`, dus de DB-laag was het niet meer — maar de
Statistics-pagina bleef seconden hangen. Oorzaak: componentcode draait op de sync-context van het
circuit, dus élke `await`-continuation in `EnergyStatisticsService` kwam op de dispatcher terug, en
die dienst loopt maand voor maand door de historie met DB-rondjes per maand. Nu draait de
dashboard-opbouw via `Task.Run` (pagina) en zijn de twee seizoensbouwers 6 uur gecached; de opbouw
logt zichzelf boven 500 ms. Openstaand: die maand-loops zelf één keer laten ophalen en in het
geheugen groeperen. Bijvangst: de `charge clamped`-logregel spamde tientallen identieke regels per
kwartier en logt nu alleen nog materiële correcties (≥250 W), één keer per kwartier.

## Gebouwd 06-08 (v1.0.45)
**Grafiek toont Charged's eigen schema als Charged de controle heeft.** Er stond al machinerie
(`ApplyChargedScheduleAsync`), maar die deugde niet: hij las één batterij en zette dat vermogen weg
als systeemtotaal (staven ~⅓ te laag), overschreef daarmee ónze planvelden — die de planner, de DB
en de rapportage voeden — en stond binnen `#if !DEBUG`, dus lokaal zag je nooit iets.
1. Nieuwe `ChargedScheduleService`: haalt `/api/v2/dynamic/schedule` bij **alle** batterijen op en
   telt per kwartier op. `ToQuarters` (blok → kwartieren; vermogen is een tempo, dus niet delen) en
   `Project` (SOC vanaf de gemeten stand, met rendement en begrenzing) zijn statisch en puur, dus
   testbaar zonder hardware — zelfde opzet als `ChargeCostBasisService.Project`.
2. `QuarterlyInfo.ChargedPlanPowerW` / `ChargedChargeLeftWh` staan **naast** ons plan (Sessy's
   tekenconventie: negatief laadt). Niets wordt meer overschreven.
3. Grafiek: onder Charged zijn hun staven de hoofdserie, ons MILP-plan een gestippelde schaduwlijn,
   en volgt de SOC-lijn hun schema. Series die onder Charged betekenisloos zijn (revenue, zero net
   home, charge needed, throttle loss) staan op `WeAreInControl`. Boven de grafiek staat wie er
   stuurt en hoe oud het schema is. Onzichtbare Radzen-series kosten geen rendertijd, dus dit werkt
   mee met de render-gating uit v1.0.40.
4. Het ophalen staat nu buiten `#if !DEBUG` (een schema opvragen is read-only); alleen het
   daadwerkelijk overdragen van de aansturing blijft in de DEBUG-guard.
Tests: `ChargedScheduleTests` (9). Let op bij tests die `DynamicScheduleItem` gebruiken:
`TimeZoneService.FromUnixTime` leest een statisch veld dat pas gevuld is nadat er één
`TimeZoneService` is geconstrueerd.

**Vergelijking beide kanten op (v1.0.46).** De batterijen plannen door, ook als wij aansturen, dus
het schema wordt nu altijd opgehaald: onder Charged via `GetChargedScheduleAsync`, anders via
`GetDynamicScheduleAsync` (zelfde endpoint, de guards in `SessyService` laten per controlemodus maar
één van de twee door). In de grafiek is de gestippelde lijn altijd het plan dat *niet* wordt
uitgevoerd — onder Charged het onze, onder eigen aansturing dat van Charged. Ophalen is afgeknepen
op één keer per 5 minuten (`MinRefreshInterval`), geteld vanaf de póging, anders wordt een batterij
die een leeg schema teruggeeft elke cyclus opnieuw bevraagd.

## Gebouwd 06-08 (v1.0.47)
**Rendement hangt van vermogen af, en de planner ziet dat nu.** Gemeten op de productie-DB stijgt
het laadrendement van 0,80 onder 1 kW naar 0,92 bij 4-5 kW: een groot deel van het omzettingsverlies
is vaste overhead, dus dezelfde energie overleeft beter in minder, vollere kwartieren. Met een
constante efficiëntie lijkt uitsmeren gratis.
1. `EfficiencyCurve` (`Services/Items/`): `rendement = plafond − overhead / vermogen`, met
   `Flat(...)` als exacte terugval op het oude gedrag. `BatteryEfficiencyService.GetEfficiencyCurveAsync`
   fit hem per richting op opeenvolgende kwartieren (AC in versus SOC-verschil), 6 uur gecached;
   `Fit` is statisch en puur, dus testbaar. Te weinig samples of een niet-fysieke helling → vlak.
2. `BatterySpec.Efficiency` gaat via `StrategyMilpService` naar `BatteryGreedyPlanner`. Alle vier de
   strategieën delen diezelfde spec en planner, dus er was geen strategie-specifiek werk.
3. In de planner geldt een bewuste asymmetrie: **beslissingen** lezen de curve op het vermogen dat
   een kwartier *aankan* (`chEffAtCapacity`), **boekhouding** (SOC-pad, objective) op het vermogen
   dat het kwartier werkelijk kreeg. Eerst geprobeerd op het blokje van 0,1 kWh — dat werkt niet:
   één blokje is in elk kwartier even klein, dus de curve zag geen verschil (test bewees dat).
4. **Bevinding uit de tests:** bij gelijke prijzen concentreerde de greedy al vanzelf (vult één
   kwartier tot de cap voor hij de volgende opent). Het uitsmeren in productie komt dus níet uit de
   doelfunctie maar uit de taper-cap op geplande energie — vastgelegd in
   `On_equal_prices_the_planner_already_fills_quarter_by_quarter`. Die cap staat op de lijst maar is
   bewust nog niet aangeraakt (zie Openstaande punten).
Tests: `EfficiencyCurveTests` (10), waaronder de discriminerende: een kwartier van 5 kW à €0,105
hoort te winnen van 1 kW à €0,100.

**Grafiek: kleur zegt wie (v1.0.47).** Oranje/groen is SessyWeb, blauw (`#7fd1ff`) is Charged, het
teken zegt wat (laden onder nul). Het niet-uitgevoerde plan is een lijn, het uitgevoerde een vlak.
Titels dragen `⟨Charged⟩` respectievelijk `⟨SessyWeb⟩`.

## Gebouwd 06-08 (v1.0.48)
**"Nu verkopen, later terugkopen" bestond niet in het model.** Klacht: 21% lading over bij
zonsopgang terwijl de avondpiek €0,305 betaalde. Nagerekend op de plandata van 06-08 is de reserve
níet de oorzaak — bij zonsopgang eiste die maar 81 Wh, en de 33%-cap (`NightReserveCapPct`) begrenst
de reserve juist naar bóven. De klem: Candidate A mag voorraad alleen verkopen als het SOC-pad
daarná nergens onder de reserve zakt, en het minimum van die keten lag op het **laatste kwartier van
de horizon** (551 Wh). Verkopen kon dus niet, want `Candidate B` eist `laden i < ontladen j` — de
omgekeerde volgorde bestond niet.
1. **Candidate D**: ontladen bij j, terugkopen bij k > j. Winst = `waarde[j] − kosten[k]/roundtrip −
   cycluskosten`; alleen de dip tussen j en k hoeft boven de reserve te blijven, want na k staat het
   pad weer op niveau. Allocatie via `bestIsRebuy`.
2. **Nachtreserve telt niet meer over een zonvenster heen.** De lus sloeg het zonvenster over en
   reserveerde voor de avond dáárna — bij zonsopgang dus voor een avond een hele zonnedag later.
   Nu wordt de verwachte zonoogst tot dat venster afgetrokken (`solarBeforeWh`). Op 06-08 zakte de
   middagreserve daardoor van 2099 Wh naar 0; op de bindende keten maakte het die dag niets uit.
Tests: `SellNowRebuyLaterTests` (4), inclusief de gevallen waarin er níet gehandeld mag worden
(verliesgevende round trip; geen dip-ruimte).

**Les uit deze sessie:** `SolarPowerPerQuarterInWatts` is ondanks zijn naam **Wh per kwartier**
(`=> SolarPowerPerQuarterHour * 1000.0`). Met W×0,25 gerekend lijkt de zonvoorspelling een factor 4
te laag en concludeer je ten onrechte dat de forecast stuk is. Gecontroleerd: voorspeld 18,6 kWh
tegen gemeten 16,0 kWh op 06-08 — die klopt gewoon.

## Gebouwd 06-08 (v1.0.49 / v1.0.50)
**Twee tegenstrijdige definities van "wie stuurt" — vijf stille storingen.** Aanleiding: met Charged
in control toonde de UI de energiestrategie uit de Sessy-portal niet. De guard bleek op een *lees*-
methode te staan (v1.0.49), en dat bleek een klasse fouten, geen los geval. Er bestonden twee
definities: `Settings.WeAreInControl` = `!(Charged || ManualOverride)` (kende de leverancier niet) en
`BatteriesService.WeAreInControl` = `!(supplier || Charged)` (kende ManualOverride niet). De UI las de
tweede, de hardware-guards in `SessyService` de eerste.
1. **Manual override stuurde niets aan.** `ExecuteManualOverride` liep via `StartCharging` c.s. op
   `SetPowerSetpointAsync`/`SetActivePowerStrategyAsync`, en die guard is per definitie `false` zodra
   ManualOverride aan staat. Elke opdracht werd geslikt; de UI toonde ondertussen het badge.
2. **Day-ahead-prijzen ververste niet buiten eigen aansturing.** `EPEXPricesService.FetchPricesFromSources`
   is de enige prijsbron en leest ze uit `GetDynamicScheduleAsync` — óók guarded. Onder Charged of
   manual override gaf die `null` → `PricesAvailable = false` → geen `StorePrices()`. Een draaiende
   instantie bleef werken omdat `_initialized` blijft plakken zodra hij één keer `true` was, maar een
   **herstart terwijl Charged stuurt** zette hem nooit meer aan: `Process()` valt dan elke cyclus uit
   op `!IsInitialized()` en er gebeurt niets meer.
3. **Curtailment bevroor.** `Evaluate()` stond binnen de in-control-tak terwijl
   `InverterCurtailmentService` elke 5 s op `CurrentAction` blijft handelen: Charged aanzetten tijdens
   een curtailment liet de omvormer onbeperkt op Shutdown/Throttle staan.
4. **Geen `ActualQuarters`** onder Charged/leverancier → gat in `PlanVsActualService`.
   (`StoreQuarterlyMeasurement` draaide wél altijd, dus taper-/efficiency-/cost-basis-fits waren heel.)
5. **Charged-handoff was dode code** sinds v1.0.6: `StartRoi`/`StartEco` draaiden alleen in de tak
   waar de POST juist dichtstond.

Opgelost met één bron: `ControlModeService` (`ControlMode.SessyWeb/Manual/Charged/Provider`,
prioriteit leverancier > Charged > manual > wij). `WeMayDriveTheBatteries` is de enige conditie op de
schrijvers — **manual override is SessyWeb die schrijft**, een ander plan, geen andere bestuurder.
Weigeren logt nu op Warning; dat stilzwijgen hield bug 1 verborgen. Lezers zijn ongeguard:
`GetDynamicScheduleAsync`/`GetChargedScheduleAsync` (identieke GET, tegengestelde guard) zijn één
`GetScheduleAsync` geworden. `Settings.WeAreInControl` is weg (berekende property, geen migratie).
`Evaluate` + `WriteActualQuarterIfNewAsync` staan nu vóór de modus-check; alleen `ExecuteAction`
blijft erachter. Handoff-tak en `StartRoi`/`StartEco`/`SetActivePowerStrategyToRoi`/`ToEco` geschrapt.
Tests: `ControlModeTests` (10) en `SessyServiceGuardTests` (7, tellen echte requests via een
`HttpMessageHandler` — dus een guard die stil slikt faalt).

## Gebouwd 06-08 (v1.0.51)
**Alles wordt opgeslagen alsof SessyWeb stuurt.** Vervolg op v1.0.50: niet de twee gaten die ik
tegenkwam, maar een sweep over álle schrijfpaden, plus het probleem dat "alles opslaan" oproept.
1. **Sweep, nagelopen niet aangenomen.** `P1MeterService`, `SessyMonitorService`,
   `ConsumptionMonitorService`, `EnergyMonitorService`, `WeatherService` en `SolarService` noemen de
   besturingsmodus nergens; hun enige poorten zijn *readiness*-checks. Bijvangst die de ernst van de
   prijzenbug van v1.0.50 vastlegt: `EnergyMonitorService.EnsureServicesAreInitialized` **gooit** als
   EPEX niet initialiseert. Een herstart onder Charged op ≤v1.0.48 stopte dus niet alleen het plan,
   maar ook `EnergyHistory` en `QuarterlyMeasurements` — de meterhistorie zelf.
2. **`ActualQuarter.ControlMode`** (migratie `AddControlModeToActualQuarter`, default `""`) tagt elk
   kwartier met wie er stuurde. Een kolom op het kwartier is robuuster dan achteraf
   `SessyWebControl`-overgangen paren; dat registreert alleen wisselingen.
3. **Taper-fit filtert.** `ThrottleAnalysisService` deelt een gepland setpoint door het gerealiseerde
   vermogen; onder Charged is dat "Charged's vermogen ÷ ons niet-verzonden verzoek". Met de
   envelope-fit uit v1.0.38, die per SOC-bin de hóógste ratio bewaart, middelt dat niet uit maar
   trekt het één kant op. Beide sample-lussen slaan nu vreemde kwartieren over via
   `IsForeign(controlMode)` — die selecteert wat eruit **moet**, zodat een kwartier zonder rij
   (vóór v1.0.51) vanzelf blijft meetellen. Let op: `QuarterlyMeasurement.BatteryMode` is óns
   geplande mode, niet wat de hardware koos, dus die filtert hier niets.
4. **Expliciet vastgelegd wat wél alles mag eten:** `BatteryEfficiencyService` (AC-in tegen
   SOC-verschil = natuurkunde) en `PlannerLearningService` (voorspelfout tegen werkelijkheid). Beide
   met reden in commentaar, anders "repareert" iemand ze later naar analogie van punt 3.
5. **Replan-lus gedempt.** De SOC-deviatietrigger (`SocDeviationThresholdPct = 20`) vuurde onder
   Charged zodra de SOC ~3,2 kWh van ons plan wegliep — geforceerde rebuild, `PlannedActions`-rij,
   plan opnieuw verankerd, gat weer open. Die trigger geldt nu alleen als wij sturen; de kwartaal-
   speculatieve solve houdt het schaduwplan vers.
6. **ENTSO-E is weer een echte fallback.** De code stond er compleet en werkend, maar werd nergens
   aangeroepen. Nu: batterijen eerst, anders ENTSO-E. Vier dingen nagerekend in plaats van aangenomen
   — eenheden kloppen (ENTSO-E EUR/MWh ÷1000, Sessy ÷100.000, beide EUR/kWh); resolutie klopte
   **niet** (PT60M geeft uurstempels terwijl alles kwartier-aligned is), dus `ExpandToQuarters`
   herhaalt de prijs over vier kwartieren — een prijs is een tarief, niet delen; `Power = 0` want
   Sessy's eigen geplande vermogen bestaat in die bron niet; en `_prices` wordt alleen nog toegewezen
   bij succes — de oude regel wiste bij één mislukte fetch alle prijzen. `PriceSource` logt en toont
   welke bron levert, en een ontbrekende `ENTSO-E:SecurityToken` logt hard bij het uitwijken.
Tests: `ControlModeDataTests` (9). Totaal 270.

**Fallback vuurde niet (v1.0.52).** De fallbackketen uit v1.0.51 was onbereikbaar in precies het
geval waarvoor hij bestaat: `FetchDayAheadPricesAsync()` had geen try/catch, dus een onbereikbare
batterij (`EnsureSuccessStatusCode`, de 5s-timeout), een lege `Batteries`-lijst (`!.First()`) of een
respons zonder `EnergyPrices` gooide een exception die langs de ENTSO-E-tak heen naar buiten vloog.
Alleen een batterij die netjes 200 mét een lege prijzenlijst antwoordde zou hem hebben getriggerd.
Nu geeft die methode bij élke storing een lege dictionary terug — dat is de enige manier waarop de
fallback bereikbaar is. Ook `SingleOrDefault` → `FirstOrDefault` op de schedule-join: dubbele
`StartTime`s in de respons gooiden anders óók.
**Regel hieruit:** een fallback is pas een fallback als de primaire bron *terugkeert* in plaats van
gooit. Bij het toevoegen van een fallback altijd eerst het faalpad van de primaire bron nalopen.

## Gebouwd 06-08 (v1.0.55)
**Charged-handoff teruggebouwd, met de afgesproken mapping.** In v1.0.51 geschrapt als dode code;
de afspraak was echter dat hij bestaat. `ProfitMaximization` → ROI (Dynamisch), `Balanced` → ECO,
en `SelfConsumption`/`BatterySaving` volgen Balanced naar ECO (ECO is Sessy's op zelfverbruik
gerichte, cyclus-armere modus). Mapping staat als `BatteriesService.MapsToRoi`.
Twee dingen die het verschil maken tussen dit en de dode versie van v1.0.6:
1. **De guard laat hem door.** `SetActivePowerStrategyAsync` krijgt `handover: true` mee — dit ís de
   overdracht, en de modus is op dat moment al geflipt, dus de normale schrijfguard zou precies de
   aanroep blokkeren die de overdracht uitvoert. Alleen `SetActivePowerStrategyToRoi/ToEco` zetten
   die vlag; al het andere blijft geguard.
2. **Hij vuurt één keer, op de overgang** (`ControlModeService.JustHandedOverToCharged`), niet elke
   cyclus — anders overschrijft hij een strategie die de gebruiker met de hand in de Sessy-portal
   heeft gezet. Bewust óók niet bij de eerste `Update` na een herstart, want dan is de vorige modus
   onbekend en draait Charged al een tijd. Praktisch gevolg: staat Charged al aan, dan moet je hem
   uit- en weer aanzetten om de overdracht te laten vuren.
Zit binnen `#if !DEBUG`, dus lokaal gebeurt er niets. Tests: `ChargedHandoverTests` (8). Totaal 278.

**Charged-series achter een checkbox (v1.0.56).** Alle zeven Charged-series staan nu achter
`ShowCharged` in de grafiekheader, **standaard uit** — zes extra series maken de grafiek onleesbaar
als je niet aan het vergelijken bent. Eén ding dat niet vanzelf goed gaat: met de toggle uit tekenen
we ons eigen plan als gevulde vlakken ongeacht wie stuurt (`!ShowCharged || !ChargedInControl`),
anders houd je onder Charged alleen de flauwe gestippelde schaduwlijnen over en zie je helemaal
niets meer. Om dezelfde reden krijgt onze SOC-lijn alleen de schaduwopmaak (blauw, gestippeld) als
de vergelijking daadwerkelijk op het scherm staat. De headertekst beweegt mee: zonder vergelijking
alleen wie er stuurt, met vergelijking de kleuruitleg plus de leeftijd van het schema.
`ToggleCharged` roept `MarkDirty()` — zonder dat doet de checkbox niets, want `ShouldRender` gate
op `_dirty` (v1.0.40).

## Openstaande punten
0. **De taper is vlak, geen aflopende lijn — hoogste prioriteit (06-08).** Hoogst gemeten laadvermogen
   per SOC-band (`QuarterlyMeasurements`, vanaf 15-06): 10-20% → 5087 W, 20-30% → 5294, 30-40% → 4987,
   40-50% → 4694, 50-60% → 4436, 60-70% → 4951, 70-80% → 5335, 80-90% → 5118, 90-100% → 3014. Dus
   ~0,78 van de 6600 W nameplate over vrijwel het hele bereik, met de knik pas boven ~85-90% SOC —
   terwijl het gefitte model (`1,054 − 0,535·soc`) op 50% SOC 0,53 zegt. De planner plant daardoor
   ~4,3 kW waar ~5,0-5,3 kW kan, en dat is waarom de batterij 's avonds op ~60-80% blijft steken.
   Twee valkuilen: de fit gooit elke sample weg waarvan `PlannedUnthrottledPowerW` 0 is (álle
   historische rijen, precies het beste bewijs), en gewoon álles meenemen geeft een fysiek
   onmogelijke *stijgende* fit omdat lage-SOC-kwartieren meestal traag zonladen zijn — dat meet
   vraag, geen kunnen. Oplossing is een envelope per SOC-band (hoogst aangetoonde ratio, monotoon
   niet-stijgend gemaakt), geen filter-tweak. Eerst de eerste eerlijke meting afwachten: een
   middagsessie die bij ~23% SOC begint met 6600 W setpoint (v1.0.38 stuurt dat nu).
1. Verifiëren op productiedata: revenue mét en zonder `CarryForwardEnabled` op teruggespeelde dagen vergelijken (niet naar SOC kijken). Faalmodus: terminal value te hoog → batterij laadt alleen nog en verkoopt nooit.
2. **Hittegolf-validatie (vanaf 28-07, ~34°C Apeldoorn).** Model is gefit op data tot 30°C, dus 34°C is extrapolatie. Toetsen: voorspelde ratio bij 50% SOC ≈ 0,52 bij temp 34 / t48 30 (tegen ≈ 0,77 op een koele dag) — dus ~3400W i.p.v. ~5100W gevraagd laadvermogen. Narekenen of de gemeten ratio's daarbij in de buurt komen en of het residu vlak blijft.
3. EF-migraties (user-kant): drop `SolarSystemShutsDownDuringNegativePrices`; `FutureValueDiscountPerHour` toegevoegd, `NearTermHedgeHours`/`Fraction` verwijderd.
4. JSON `Infinity`/`NaN` crash (22-07 17:22, MVC-controller) + lege Enever gasprijs-feed.
5. Comment-opschoning was bezig toen onderbroken — meeste lange blocks al ingekort, mogelijk nog een paar razor/test-comments over.

## Diagnosed, niet-een-bug
- Setpoint requested ≠ Setpoint: Sessy-hardware klemt/tapert zelf (CC/CV, SOC-afhankelijk). API meldt geen reden. `Battery.SetpointRequested` (ons) vs `Sessy.PowerSetpoint` (device).
- 21-07 avond niet-ontladen: economisch correct (export tegen €0,30 was netto verlies vs. latere terugkoop); de niet-uitvoering die avond was de `j=1`-bug op v1.0.4.

**Te verifiëren na de eerstvolgende productie-run van v1.0.48 (uitgerold 06-08 avond):**
1. De middagsessie: setpoint 6600 W vanaf ~23% SOC — wat levert de hardware? Dat beslist over
   Openstaand punt 0.
2. Ontlaadt de avond dieper door Candidate D, en komt de SOC bij zonsopgang lager uit dan de 21%
   van 06-08?
3. Verschijnt "Efficiency curve fitted on N samples" in het log? Zo niet, dan draait de planner nog
   op de vlakke terugval en doet het vermogensafhankelijke rendement niets.
4. Blijven `DbHelper: slow`, `ThreadPool busy` en "UI blocked: clock tick ran N ms late" stil?
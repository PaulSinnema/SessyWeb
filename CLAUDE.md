# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

SessyWeb is a home energy management system (HEMS) for households with [Sessy](https://www.sessy.nl) home batteries. It runs as a Docker container on a NAS and plans battery charge/discharge over a 72-hour horizon against day-ahead EPEX prices, solar forecast and consumption forecast. Everything is local — SQLite, no cloud beyond the ENTSO-E and WeerLive API calls.

The planner is a **deterministic greedy search** (`BatteryGreedyPlanner`), not a MILP — there is no OR-Tools reference anywhere in the solution. The `Milp*` class names and the "solver" wording in `MilpServiceBase` are leftovers from the original design; see "Openstaande punten" 4 for the DP that would replace the heuristic.

Target framework is **net10.0** across all projects; the Dockerfile uses `mcr.microsoft.com/dotnet/aspnet:10.0`.

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
- **Add a `CHANGELOG.md` entry with that same bump**, newest version at the top, grouped as Added/Changed/Fixed/Removed. Keep it to what a user notices, in English, a few lines per version — the reasoning, the measurements and the rejected alternatives belong in this file instead. A change with nothing observable (a refactor, a test) still gets its version bump but needs no changelog entry.
- Comments and log messages are a mix of English and Dutch; match the surrounding file.
- Region separators use the `// ── Name ─────` box-drawing style.

## Project layout

| Project | Role |
|---|---|
| `SessyWeb` | Blazor Server UI (Radzen), `Program.cs` (all DI wiring), API controllers, EF migrations run here |
| `SessyController` | Domain + background services: the planner, hardware polling, state machine, inverter drivers |
| `SessyData` | EF Core / SQLite: `ModelContext`, entity models, one `*DataService` per entity |
| `SessyCommon` | Config POCOs, extensions, `TimeZoneService`, `ServiceLocator`, `DockerService` |
| `Djohnnie.SolarEdge.ModBus.TCP` | Vendored SolarEdge Modbus library |
| `SessyUnitTests` | xunit v3 + Moq |

## Architecture

### Startup and DI (SessyWeb/Program.cs)

Every service is registered here — this file is the map of the system. Key points:

- Config is loaded from `$CONFIG_PATH/appsettings.json` (+ optional `secrets.json`), **not** the project's own appsettings — true only since v1.0.78, which removes the built-in `appsettings.json` from the sources and stops publishing it. `CONFIG_PATH` defaults to the working directory. Sections bind to `SessyBatteryConfig`, `SessyP1Config`, `PowerSystemsConfig`, `SettingsConfig`, `WeatherExpectancyConfig`, `SolarEdgeCloudConfig`, `HeatPumpConfig`.
- **`SettingsService` must remain the first `AddHostedService`.** All other background services `await _settingsService.WaitForReadyAsync()` before their first cycle.
- Most services are singletons registered *both* as themselves and as their interface via a `sp => sp.GetRequiredService<T>()` factory — never add a second `AddSingleton<IFoo, Foo>()` alongside, that creates a duplicate instance.
- `ServiceLocator.ServiceProvider` is set post-build for the few places that resolve outside DI.

### Settings: two separate config systems

1. **`appsettings.json`** — infrastructure only: connection string, backup dir, battery/meter/inverter IPs. (Timezone moved to the DB row in v1.0.87; `SettingsConfig` holds only `DatabaseBackupDirectory`.) Read via `IOptionsMonitor<T>`; changes fire `OnChange`.
2. **The `Settings` DB row** — all operational tuning (strategy, cycle cost, reserve %, efficiency, consumption profile, manual override). Owned by `SettingsService`; read via `_settingsService.Current`; after the UI saves, call `SettingsService.RefreshAsync()` which fires `SettingsChanged(Settings, bool isStartup)`.

Services cache `Settings` in a field and refresh it in the `SettingsChanged` handler. When adding a setting that must trigger a plan rebuild, set a rebuild reason in that handler (see `MilpServiceBase._configChangedReason`).

### Planning pipeline

`BatteriesService` (the main loop, a `BackgroundHeartbeatService`, 60s / 10s in DEBUG) drives each cycle:

1. Gather `List<QuarterlyInfo>` — one object per quarter-hour holding price, solar, consumption, mode and SOC. `QuarterlyInfo` is the central data structure passed between the planner, the UI and the DB writers; measured quarters are constructed from a `QuarterlyMeasurement` (`IsMeasured = true`), future quarters via `CreateAsync`.
2. `IMilpService.BuildPlanAsync(quarterlyInfos, currentSocWh)` — rebuilds only when needed (price change, SOC deviation > 20%, settings change).
3. `EnergySystemStateMachine.Evaluate(EnergySystemInput)` decides the actual mode.
4. Execute one action per quarter via the Sessy Open API (`SessyService` / `BatteryContainer` / `Battery`).

**Strategy selection**: `IMilpService` is registered as `MilpServiceProxy`, which dispatches per call to `ProfitMaximizationMilpService`, `SelfConsumptionMilpService`, `BalancedMilpService` or `BatterySavingMilpService` based on `Settings.Strategy` — so a strategy change in the UI takes effect without a restart. All four derive from `MilpServiceBase` (~990 lines: input gathering, SOC bookkeeping, plan persistence — the search itself is `BatteryGreedyPlanner`); the per-strategy deltas live in `Services/Optimization/Strategies/`. Put shared planner changes in `MilpServiceBase`, objective/constraint differences in the strategy.

### State machine

`EnergySystemStateMachine` is the single place where battery mode and inverter setpoint are decided — "all transition logic lives here, nowhere else". Curtailment (negative selling price) overrides the plan. Two separate enums, do not conflate them: the battery mode is `Modes` (`Unknown`, `Charging`, `Discharging`, `ZeroNetHome`, `Disabled`), the curtailment action is `CurtailmentMode` (`None`, `ZeroExport`, `Throttle`, `Shutdown`). `InverterCurtailmentService` polls `CurrentAction` every 5s. It has no DI dependencies beyond a logger, which is why it is the most heavily unit-tested class — extend `EnergySystemStateMachineTests`' input matrix when adding a transition.

### Data access

`DataService` classes derive from `ServiceBase<T>` (`Add`, `AddOrUpdate`, `Update`, `Remove`, `Get`, `GetList`, `Query`, `RemoveWhere`) and go through `DbHelper`, which creates a fresh scope + `ModelContext` per call. Sinds v1.0.40:

- **Schrijven** loopt door één **statische** `SemaphoreSlim` — proces-breed, want SQLite laat maar één schrijver toe. (Vóór v1.0.40 stond hier "één proces-brede semafoor voor álle toegang"; dat klopte niet: `DbHelper` is `AddScoped` en `ServiceBase` maakt een eigen scope, dus het slot gold per data-service. Het enige echt globale slot zat in SQLite zelf.)
- **Lezen** loopt via `ExecuteQueryAsync` (max 4 tegelijk per data-service) en blokkeert geen thread meer. De oude synchrone `ExecuteQuery` met `Wait()` parkeerde een thread-pool thread per query — de directe oorzaak van seconden-lange GUI-haperingen.
- Nooit een `ModelContext` over aanroepen heen vasthouden; nooit een data-service aanroepen *binnen* de callback van een andere schrijfactie (deadlock op het statische schrijfslot).
- Reads use `AsNoTracking()`, so returned entities are detached — write back through `AddOrUpdate`/`Update`.
- `AddOrUpdate`/`Update`/`Remove` require `T : IUpdatable<T>` and use that `Update()` implementation to copy fields.
- `ExecuteTransaction` throws if `SaveChangesAsync` writes 0 rows. `RemoveWhere` (bulk delete) draait daarom via `ExecuteWriteAsync` zónder transactie — `ExecuteDelete` gaat buiten de change-tracker om en zou anders teruggerold worden.
- Grote upserts: geef `ServiceBase.MatchOn(key, window)` mee aan `AddOrUpdate` in plaats van een `contains`-lambda die per rij een query doet.
- Verbindingen krijgen `synchronous=NORMAL` en `busy_timeout=5000` via `SqlitePragmaInterceptor`; `journal_mode=WAL` staat **niet** daar maar wordt één keer bij het opstarten gezet via `SqliteSetup.EnableWriteAheadLogging` (het is een schrijfactie — zie v1.0.41).

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
C#/.NET Blazor Server EMS. Stuurt 3× Sessy batterij (cap 16,2 kWh; raw charge 6600W/discharge 5100W; **aantoonbaar gehaald ~5,0-5,3 kW laden** over vrijwel het hele SOC-bereik — zie Openstaande punten, de eerdere "praktijk max ~4,4kW" was de getaperde planwaarde, niet de hardwarelimiet), SolarEdge inverter, Daikin warmtepomp. Draait op Synology NAS via Docker. Huidige versie **v1.0.97**. Locatie: Apeldoorn.
**Let op:** dit document beschrijft t/m v1.0.56 en dan weer vanaf v1.0.72; v1.0.57-v1.0.71 (o.a. de
GHCR-overstap en de .NET 10 / Blazor-buildfix) staan er niet in.
Sinds v1.0.78 draaien er ook instanties bij anderen — meldingen komen als GitHub-issues binnen op
`PaulSinnema/SessyWeb`. Configuraties daar wijken af (één batterij, geen zon, geen warmtepomp); dat
is de bron van de hele v1.0.78-97-reeks.

## Werkafspraken
- Antwoorden in het Nederlands, caveman-ultra kort. Code-commentaar in het Engels, **kort — één regel waar mogelijk** (user leest alle comments na ter controle).
- **Niet gissen**: eerst verifiëren in code/DB/web. Meerdere sessies gingen mis door aannames — meerdere keren "gevonden!" geroepen en ernaast gezeten. Bij planner-analyse: DB-tabellen naast elkaar leggen en de opgenomen solve-invoer door de échte planner terugspelen vóór conclusies (zie Omgeving en v1.0.94).
- Code-behind boven `@code`-blokken. Radzen Blazor overal. Radzen API's verifiëren via `raw.githubusercontent.com/radzenhq/radzen-blazor/master/...` (rendering-pad én crosshair/tooltip-pad — `CartesianSeries.DataAt/TooltipY` unwrapt `double?` hard).
- Na codegeneratie altijd zelf reviewen op syntax/naamfouten/dubbele code/missing usings. Braces + code-parens balanceren (comments negeren bij paren-telling).
- **Versie ophogen bij ELKE wijziging, plus een CHANGELOG-regel** — één afspraak, hierboven onder "Repository conventions". Niet hier herhalen; twee kopieën lopen uit elkaar.

## Omgeving
- Broncode én werkkopie: `C:\Projects\Sessy` (Windows, PowerShell, dotnet aanwezig — bouwen en testen kan direct).
- Productie-DB staat lokaal: `SessyWeb/SessyController/Data/Sessy.db` (+ WAL/shm). Read-only openen voor analyse.
- Planner-onderzoek gaat via de **échte** planner, niet via een spiegel: `SolveInputRecorder`
  (`SESSY_RECORD_SOLVE_INPUTS`) neemt de solve-invoer op, en de opgenomen JSON speelt terug door
  `BatteryGreedyPlanner` zelf (zie v1.0.94 en `RealDayPlannerTests` / `EveningDischargeProbeTests`).
  De oude Python-spiegel is weg — die liet door een afwijkende lusgrens (`j=1` vs `range(0,n)`) een
  bug ten onrechte als "opgelost" melden.

## Planner-architectuur (BatteryGreedyPlanner.cs)
Deterministisch, greedy, twee passes: (1) ZeroNetHome-baseline, (2) arbitrage in blokken van `BlockKWh` — 0,20 kWh sinds v1.0.77 (was 0,10; puur een snelheidsknop, de plannen zijn bit-identiek van 0,05 t/m 1,00 kWh). Candidate A = ontladen van al-aanwezige energie (geen gekoppeld laadmoment); Candidate B = laden-i → ontladen-j met `i<j`.
- **FutureValueDiscountPerHour** (default 0,003 — UI toont procenten, dus 0,3): continue korting op waarde[j]/kosten[i], vervangt de oude discrete "near-term hedge" die drie keer op randgevallen stukliep. Rapportage/objective gebruiken echte prijzen. Default afgeleid uit gemeten planstabiliteit in `PlannedActions` (94-97% tot 18 u lead → 0,002-0,006/u). Hoort samen met `PredictedPriceMode`: bij `Off` is >24 u toch `reserveOnly`; zet je die op `SoftMargin`, dan moet de discount omhoog want daar houdt maar 23-63% stand.
- **ReplacementCostEurPerKWh**: bodemprijs (reservatieprijs) voor Candidate A — voorkomt dumpen van doorschuif-energie. Kwam als `StockCostEurPerKWh` uit het FIFO-gemiddelde; sinds v1.0.30 levert `ReplacementCostService` het getal en is FIFO alleen nog terugval. Zon-voorraad = 0.
- Candidate C (carry-forward voorbij de horizon, v1.0.30) en Candidate D (nu verkopen, later terugkopen, v1.0.48) staan bij hun eigen versies beschreven.

## Grote bugs opgelost (v1.0.4 t/m v1.0.16)
1. **`j=1` → `j=0`** (BatteryGreedyPlanner): arbitrage-loop sloeg index 0 (= huidig kwartier) over. Sinds horizon bij `nowQuarter` begint, schoof elke ontlading eeuwig één kwartier vooruit → nooit uitgevoerd. Kernbug.
2. **Speculative-solve rollback** (MilpServiceBase v1.0.14): `ApplySolveResult` installeerde plan vóór accept/reject; een afgewezen solve bleef de batterij aansturen zonder spoor in DB. Nu snapshot `_planByTime`/`_planSocWhByTime` vóór solve + herstel + `WriteBackSocSimulationAsync` bij reject.
3. **SOC-deviatie meetfout** (v1.0.13→1.0.16): vergeleek live mid-kwartier SOC met END-of-quarter plan-SOC → spookafwijking ~7-13% tijdens (dis)charge → onnodige forced replans. v1.0.13 interpoleerde maar was gebroken (horizon start bij nowQuarter → geen voorganger, altijd fallback). v1.0.16 fix: `_planStartSocWh`/`_planStartQuarter` bijgehouden in ApplySolveResult (let op: param `socKWh` is kWh, ×1000 → Wh), meegenomen in rollback-snapshot en ClearPlanAsync.
4. **Guards weigerden i.p.v. clampen** (v1.0.7): laad-guard gebruikte vol vermogen niet gepland; ontlaad-guard had hardcoded `+50Wh`. Bovenste ~1,6 en onderste ~0,5 kWh onbereikbaar. Nu clampen naar beschikbare ruimte/energie, const `MinimumUsefulWh=25`. Alle NZH/Disabled-takken loggen op Warning (prod log-level = Warning).
5. **Sticky mutations** verwijderd: guards schreven downgrades permanent in `_planByTime` → één ruizige meting bleef het hele kwartier plakken.

## Revenue/FIFO-correcties (v1.0.11-1.0.12)
Laadkant waardeerde overal álle lading tegen inkoopprijs, ook gratis zon → verzonnen negatieve "revenue"-labels. Gefixt: `QuarterlyInfo.Profit` (beide kanten gesplitst via NetLoadWh), `MeasurementView.GridChargeCostEur` (= `min(GridImport,Charged)×buy`, spiegelt DischargeValueEur), `EnergyStatisticsService` twee plekken op één conventie, dode `CalculateAveragePriceOfChargeInBatteries` (65 regels) verwijderd. `Profit` wordt als `QuarterlyMeasurement.PlannedRevenueEur` in DB geschreven — historische rijen houden oude waarden (user koos niet-herberekenen).

## Overige features
- **Charged handoff** (v1.0.6, geschrapt in v1.0.51, teruggebouwd in v1.0.55): bij `ChargedInControl` → Sessy native strategie. Mapping en guard staan bij v1.0.55; de mapping uit v1.0.6 (SelfConsumption→ECO, anders ROI) geldt niet meer.
- **Plan history overlay** in ChargingHoursChartComponent: valt samen met "Show all", dropdown toont plannen binnen hetzelfde `[from,to)`-venster. Solide magenta lijn, bovenste serie.
- **Actual power series**: areas in plan-kleuren, gebonden aan gefilterde `ActualPowerPoints` (`HasActualPower`) zodat ze bij "nu" stoppen i.p.v. plan dubbel te tekenen.
- **Last-plan reason** zichtbaar in header onder SOC dev.
- **Curtailment unit tests** (`CurtailmentPricingTests.cs`): netting on/off × pos/neg marktprijs × belasting/vergoeding/BTW.
- Obsolete "Shut down at negative prices" verwijderd (superseded door InverterCurtailmentService).

## Doel van de gebruiker
Investering z.s.m. terugverdienen, winst leidend. (De zelf-lerende planner die hieruit volgde is
gebouwd in v1.0.30 — zie die sectie.)

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
3. Grafiek: onder Charged zijn hun staven de hoofdserie, ons plan een gestippelde schaduwlijn,
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
   `On_equal_prices_the_planner_already_fills_quarter_by_quarter`. Die cap is in v1.0.94 aangepakt
   met `ChargeCapabilityFloor`; toen stond hij nog open.
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
op `_dirty` (v1.0.40). **Vervangen in v1.0.79** — zie onderaan.

## Gebouwd 08-08 (v1.0.72)
**Ontlaadkant liep op temperatuur en zat bevroren op 60%.** Aanleiding was de vraag of throttling
ook met (dis)charge power correleert. Nagemeten op de productie-DB: **nee** — elke vermogensproxy
(gemiddeld vermogen vorig 1/2/4 u, sessie-energie, kwartieren in sessie) is insignificant op de
envelope-samples die productie gebruikt (|t| ≤ 1,66, R² 0,883 → hooguit 0,889), en het teken is
*positief*, tegengesteld aan de warmte-hypothese: het meet "zware sessies zijn sessies waarin de
batterij zwaar kán". Ontlaadkant idem (t = −0,28). **Geen vermogensterm in het model**; dat staat
als reden in `DischargeCapability`, anders bouwt iemand hem later terug.

Wat er wél uit kwam: de ontlaadkant miste SOC. Temperatuur alleen verklaart R² 0,012 (t = 0,93);
mét SOC 0,225 (t = **+4,43**), teken positief — lage celspanning koopt minder vermogen bij dezelfde
stroomlimiet. Drie fouten stapelden:
1. **Verkeerde regressor.** Temperatuurbuckets waar SOC het signaal draagt.
2. **Gemiddelde i.p.v. envelope.** `GetThrottleBucketsAsync` gebruikt een EMA → ratio ≈ 0,60
   (3060 W) terwijl ~4100 W aantoonbaar haalbaar is. Dit is de meetfout die v1.0.38 op de laadkant
   met `SelectEnvelope` oploste; de ontlaadkant was nooit meegenomen.
3. **De lus was bevroren, niet scheef.** Het setpoint was de getaperde planwaarde en `Unthrottle()`
   reconstrueerde de noemer als `plan / ratio`. Bij ratio r: gevraagd 5100·r, geleverd 5100·r,
   noemer 5100, nieuwe ratio r. **Elke r is een dekpunt** — de ontlaadratio kon zich nooit
   herstellen, wat de hardware ook kon.

Nieuw: `DischargeCapability` (plateau + knik in absolute watts, géén temperatuurtermen) en
`ThrottleAnalysisService.GetDischargeCapabilityAsync(nameplateW)`. De respons is `|BatteryPowerWatts|`
zonder noemer, dus **de hele historie is bruikbaar** in plaats van alleen kwartieren mét
`PlannedUnthrottledPowerW` (748 tegen 74). Envelope = max per 5%-SOC-bin; plateau = mediaan van de
bin-maxima boven half vol, geklemd op nameplate; knik = laagste bin waar die bin **én** de volgende
het plateau halen — twee op rij, zodat één toevallige bin laag in het bereik niet claimt dat er geen
terugval is. Productiefit: **plateau 4121 W, knik 0,20, 19 bins**. Bin 10-15% (één losse 4104) en de
95-100%-uitschieter (416 W) worden zo correct genegeerd.
Doorgegeven via `BatterySpec.DischargeCapability` naar `cappedDischargeKWh(t, socStart)` in
`BatteryGreedyPlanner`, spiegel van `taperedChargeKWh`. `PlanStep.RequestedDischargeKW` naast
`RequestedChargeKW`; `MilpServiceBase.RequestedDischargePowerW` vervangt `Unthrottle()`, dat samen
met `_throttleRatioByTime` is verwijderd. Bij `Samples == 0` blijft de oude bucketweg staan, zodat
een verse DB niet ineens op nameplate plant.
Grafiek "Battery throttle vs outside temperature": de ontlaadlijn was door de reconstructie
zelfvervullend en wordt nu een echte meting. Onderschrift aangepast — de ontlaadlijn is nog slechts
diagnostiek, de planner stuurt daar op SOC.
Tests: `DischargeCapabilityTests` (11). Totaal 289.
Losse bevinding: `ThrottleAnalysisService.GetDischargeRatio`/`GetChargeRatio` (de niet-`Try`-varianten)
en `FindRatio` hebben al vóór deze wijziging geen aanroepers — dode code, laten staan.

## Opgelost 08-08 (v1.0.72 t/m v1.0.75) — voormalige openstaande punten 1 t/m 5

**Punt 1 — carry-forward is een no-op op deze data.** 75 aaneengesloten dagen (26-05 t/m 08-08)
teruggespeeld door de échte `BatteryGreedyPlanner` (geen Python-spiegel), rollend: 48 u-horizon,
eerste 24 u afgerekend, SOC doorgeschoven, twee runs die alleen in de vlag verschillen. Beide
plannen afgerekend met één evaluator die als zelfcontrole exact de objective van de planner
reproduceert (0,5562 = 0,5562) bij carry-forward uit. Resultaat **€ 0,00 verschil**, identiek op
import (471,1 kWh), export (605,7), ontladen (999,2) en eind-SOC (3,64); de plannen wijken op 1 van
de 75 dagen af (12-06) en die afwijking valt buiten het uitgevoerde venster. Oorzaak: replacement
cost ~€ 0,13/kWh, over 48 u verdisconteerd ~€ 0,114, en de all-in inkoopprijs zakt daar op maar 7
van 68 dagen onder — juist dan wint Candidate B (avondpiek ~€ 0,30) en is de batterij vol vóór C aan
bod komt. Faalmodus niet opgetreden, ook niet geforceerd: bij replacement cost ×1,5/×2/×3 (tot
€ 0,39/kWh, mediaan-cap omzeild) blijft het effect −€ 0,57 op 75 dagen en blijven er 0 dagen zonder
ontlading. **Vlag mag aan blijven (staat in productie op 1), maar niet als winstbron rekenen.**
Beperkingen: vlakke reserve, geen taper/efficiency-curve/discharge-capability, 48 u i.p.v. 72 u, en
alle kwartieren verhandelbaar terwijl productie `PredictedPriceMode = Off` heeft — dit was dus
carry-forwards *beste* geval. Alleen zomerdata.

**Punt 2 — hittegolf-validatie** opgegaan in punt 0: het probleem is niet dat 34 °C extrapolatie is,
maar dat de fit *uitsluitend* hittegolfdata ziet.

**Punt 3 — EF-migraties waren al klaar sinds 15-07**, nooit handwerk nodig geweest.
`20260715103547_RemoveShutDownAtNegativePrices` en `20260715131646_RemoveNearTermHedge` staan in
`__EFMigrationsHistory`; migraties draaien automatisch bij het opstarten.

**Punt 4 — JSON `NaN`-crash + gasprijs-feed (v1.0.74).**
`SunspecInverterService.GetACPowerInWatts` geeft `double.NaN` bij een corrupte Modbus-scale-factor.
Die ging direct in de publieke `ActualSolarPowerInWatts` — de bestaande guard filterde alleen de
*historie*. Van daar via `+=` in `SolarInverterManager` naar
`GET /BatteryManagement/Inverter/AcPowerInWatts`, waar System.Text.Json een niet-eindige double
weigert → 500; én naar `HardwareStatusService`, waar elke vergelijking met NaN stilzwijgend false is.
Drie plekken gerepareerd: pas publiceren als de meting eindig is (laatste goede waarde blijft staan,
met Warning) en niet-eindige bijdragen overslaan in `SolarInverterManager` én
`SunspecInverterService.GetTotalACPowerInWatts`. Bewust géén `AllowNamedFloatingPointLiterals`: dat
maakt er `"NaN"` als string van, wat Loxone net zo hard breekt en de fout verbergt.
`QuarterlyInfo` nagelopen — die delingen zijn al afgeschermd.
*Gasprijs:* `FetchGasPriceAsync` beloofde in haar eigen comment "leaves the previous value intact on
failure" maar deed dat alleen bij `status != true` en een lege data-array; bij een parse-fout, een
ontbrekend `prijsEGSI`-veld of een exception viel hij dóór met `marketPrice` op 0,00 en publiceerde
belasting-alleen als gasprijs. Nu een expliciete `havePrice`-vlag.

**Punt 5 — comment-opschoning (v1.0.75).** Het restant bleek geen *te lange* maar **foute**
documentatie: 11 verweesde `<summary>`-blokken, docs van verwijderde methodes die op de volgende
member waren blijven plakken (`CalculationService` de gasprijs-methode boven de batch-prijsmethode,
`EPEXPricesService` `GetPrices()` boven `GetSellingPriceForQuarter`, `BatteriesService` de
SOC-envelopes boven `WriteActualQuarterIfNewAsync`, `EnergyStatistics` "energy charged" boven
`StartSocKWh`). Per geval de doc gehouden die bij de member eronder hoort — in `ChargingHoursPage`
was dat juist de éérste, dus niet mechanisch te doen. In `QuarterlyMeasurement` twee `<summary>`-tags
op dezelfde property samengevoegd. Verder weg: 22 regels uitgecommentarieerde code in
`SolarPowerPage.razor.cs` en het "Add these endpoints to…"-scaffoldingblok.
**Bewust blijven staan:** de lange klasse-docs van de tests (`RealDayPlannerTests` met de
productiecijfers van 30-07 enz.) en de Radzen-crosshair-uitleg in `ChargingHoursChartComponent` —
dat is de vastgelegde reden waaróm die tests en guards bestaan. Ook de uitgecommentarieerde
`.LogTo(...)` in `ModelContext.OnConfiguring`, een bewuste debug-toggle.

**Resize hertekende de layout drie keer te vaak (v1.0.76).** `MainLayout.OnResizedAsync` riep twee
keer achter elkaar `await InvokeAsync(StateHasChanged)` aan — kopieerfout — en deed dat óók voor
resize-events die `ScreenInfo.Update` als ruis wegfiltert (iOS-adresbalk, band van 4 px). Rendering
van de layout rendert `@Body` mee, dezelfde reden waarom `SetIsBusy` via de overlay loopt, dus op de
charging-hours-pagina was dat twee volledige hertekeningen van een grafiek met ~17 series over
288 kwartieren, per genegeerd event. `Update` geeft nu `bool` terug (veranderd ja/nee) en
`OnResizedAsync` stopt als er niets veranderde; één `StateHasChanged` blijft over. De
`Console.WriteLine($"ScreenInfo: …")` per resize per circuit is weg — dat was een rauwe
console-schrijf, dus zelfs het Warning-logniveau onderdrukte hem niet.

## Gebouwd 08-08 (v1.0.77) — planner-doorlichting op efficiency

**De zoektocht was kubisch in de horizon.** Per arbitrage-iteratie liep de `j`-lus (n) × Candidate B's
`i`-lus (n) × de haalbaarheidsscan `for k = i..j` (n); Candidate D deed hetzelfde nog eens. Alles wat
alleen van `i` of alleen van `j` afhangt is binnen één iteratie constant (`socEnd` beweegt niet
terwijl één blok wordt gekozen), en de drie scans zijn lopende minima. Nu één keer per iteratie
gevuld in arrays (`chargeCap`, `chEffCap`, `disCap`, `disEffCap`, `slack`, `room`) plus
suffix-minima (`minSlackFrom`, `minRoomFrom`) en per `j` één achterwaartse pas (`roomMinTo`).
Candidate D houdt een lopend minimum bij terwijl `k` oploopt. **`i` loopt nog steeds oplopend** —
dalend zou gelijke winsten anders breken (de vergelijking eist strikt groter, dus bij gelijkspel
wint de eerste `i`).
Gemeten op 75 dagen, traagste solve: 24 u 161→34 ms, 48 u **1645→279 ms**, 72 u 3529→721 ms.
Bewijs dat er niets aan de beslissingen veranderde: een SHA-256 over álle geplande kwartieren van
alle 75 horizons is identiek vóór en ná (`D0C801D4C02CC4BA` bij 48 u, `7A742943E4FAA41E` bij 24 u,
`CF804AAF5BD8F66E` bij 72 u), en de euro's tot op de cent. In productie is de winst gróter dan hier
gemeten: de probe geeft `ChargeTaper`, `Efficiency` en `DischargeCapability` als null mee, terwijl
juist die de gehoiste aanroepen duur maken.

**`BlockKWh` 0,10 → 0,20.** Blijkt een pure snelheidsknop, geen nauwkeurigheidsknop: `profitPerKWh`
hangt niet van de blokgrootte af en elke limiet wordt vóór toewijzing geklemd, dus een grovere stap
bereikt dezelfde allocatie in minder iteraties. Over 75 dagen zijn de plannen **bit-identiek van
0,05 t/m 1,00 kWh** terwijl de tijd omgekeerd schaalt (0,05 → 22 s, 0,10 → 11 s, 0,20 → 5,9 s,
1,00 → 2,2 s). Bewust niet de snelste waarde genomen: zomerprijscurves zijn geen bewijs voor elke
curve, dus 0,20 houdt een factor vijf marge.

**Gemeten en NIET gewijzigd:**
- *Candidate A's replacement-cost-bodem is onverdisconteerd* terwijl `valueJ` dat wél is (de bodem
  is de tegenhanger van Candidate C's `carryValue`, die wél `Discount(n)` krijgt). Reëel in de code,
  maar economisch inert op deze data: bij een replacement cost van 0,10 / 0,1303 / 0,16 / 0,20
  veranderen de plannen wel (andere fingerprints) en de opbrengst niet — 0,00 / 0,00 / +0,22 / 0,00,
  niet-monotoon, dus planherschikking en geen signaal. Niet aangeraakt zonder bewijs.
- *De discount-sweep is met deze harnas niet te beoordelen.* 0 → +€0,45, 0,001 → +€0,35,
  0,006 → −€0,91, 0,01 → −€3,63 lijkt te zeggen "zet hem lager", maar de replay rekent af tegen
  dezelfde forecast waarmee gepland is: perfecte vooruitblik, en dan is een hedge tegen
  voorspelfouten per definitie alleen maar kostenpost. Om de discount eerlijk te toetsen moet er
  afgerekend worden tegen *gemeten* verbruik en zon (`QuarterlyMeasurements` / `EnergyHistory`).

**Bijvangst — greedy-suboptimaliteit is nu meetbaar.** Met een rollende 24 u-commit levert een
horizon van 72 u **minder** op dan 48 u (−€0,84) en 24 u nog minder (−€1,37). Onder perfecte
vooruitblik hoort meer informatie nooit slechter te zijn; dat het hier wel zo is, is een direct
symptoom van de heuristiek. Een DP over (kwartier, SOC-niveau) zou hier het echte optimum geven —
naar schatting ~9M toestandsovergangen, dus sneller én beter — maar dat is een herbouw van de kern.

**Stale comment weg:** bij `pairRoundTrip` stond "at the power both quarters would run at with the
block added … filling one up beats opening another", terwijl `roundTripAtCapacity` de *cap* van het
kwartier las. Dat de beslissing op de cap leest is bewust (v1.0.47) en staat toegelicht boven
`chEffAtCapacity`; de tweede comment sprak de eerste tegen.

## Gebouwd 10-08 (v1.0.78 / v1.0.80 / v1.0.86 / v1.0.87) — config en opstartvolgorde

**Ingebakken appsettings.json overschreef de gemonteerde config niet — hij vulde hem aan (v1.0.78).**
Melding van buiten: met één batterij geconfigureerd bleef de app naar batterij 2 verbinden
(`Could not get power status after 3 tries for battery 2`, HttpClient-timeout van 5 s). Geen
hardgecodeerde 3 — `BatteryContainer` loopt gewoon `foreach` over `Sessy:Batteries:Batteries`.
Oorzaak: de SDK publiceert `SessyWeb/appsettings.json` (mijn eigen werkende config, batterijen
"1","2","3" op 192.168.1.241-243) mee naar `/app` = content root, en `CreateBuilder` laadt die
automatisch vóór `Program.cs` de `CONFIG_PATH`-versie toevoegt. .NET-configuratie merget **per
sleutel**, het vervangt geen secties: sleutel "1" werd overschreven, "2" en "3" bleven staan. Zelfde
mechanisme gold voor `Sessy:Meters` (P1 op .240), `PowerSystems` (SolarEdge op .217 met mijn twee
panelengroepen), `WeerOnline:Location`, `HeatPumpConfig`, `ManagementSettings` (timezone) en
`ConnectionStrings`. (De `"ChargedInControl": true` die daar ook stond bond nergens op — zie
v1.0.81.) De regel in dit document dat de config "uit
`$CONFIG_PATH` komt, **niet** uit de eigen appsettings" was dus niet waar in de image.
Twee remmen: (1) `RemoveBuiltInAppSettings` haalt vóór het toevoegen elke `JsonConfigurationSource`
met bestandsnaam `appsettings.json` uit `builder.Configuration.Sources` — env-overlays
(`appsettings.Development.json`) blijven, die zijn geen configuratietemplate en worden in de
container (Production) niet geladen; (2) `<CopyToPublishDirectory>Never` op `appsettings.json` in
`SessyWeb.csproj`, geverifieerd op een echte publish. **Gedragsverandering:** ontbreekt de
config-volume, dan draait de app niet meer stil door op mijn IP-adressen maar faalt hij zichtbaar.

**Nasleep: een ontbrekende sectie bestond nooit eerder (v1.0.80).** Zodra de template niet meer
meegaat, is "sectie afwezig" een echt geval. Bij een gebruiker met één batterij en géén zonnepanelen
crashte de host meteen: `SolarInverterManager.FillActiveInverterServices` deed
`_powerSystemsConfig.Endpoints.ContainsKey(...)` in de constructor → `NullReferenceException` vóór
er één service draaide. Bron aangepakt in plaats van die ene regel: `PowerSystemsConfig.Endpoints`,
`SessyP1Config.Endpoints` en `SessyBatteryConfig.Batteries` zijn nu niet-nullable met een lege
dictionary als default, want elke consument itereert of doet een lookup. Verder in dezelfde sweep:
`SunspecInverterService.Endpoints` gebruikte een indexer op de providernaam (DI construeert álle
inverterservices, ook die niemand configureert) → `TryGetValue`; `ThrottleInverterToWatts` gooide
"InverterMaxCapacity not set or wrong in config" bij nul inverters → geen fout, gewoon niets te
regelen. Nagelopen en al goed: curtailment (`IsAvailable` is `false`, en `CurrentInverterSetpointW`
blijft `MaxValue` dus `ReleaseAsync` keert vroeg terug), `SolarService` (0-guards),
`EnergyStatisticsService` (`kWp <= 0` → terugval), `SolarEdgeCloudConfig` en `HeatPumpConfig` (beide
melden netjes "niet geconfigureerd"). Bijvangst: `P1MeterContainer.AddMeters` riep `P1Meters.Clear()`
**binnen** de lus — bij twee meters overleefde alleen de laatste, en bij een config-reload naar een
lege sectie werd er aangevuld in plaats van geleegd. Tests: `NoSolarConfigurationTests` (6).
Totaal 295.

**Tijdzone alleen nog in de DB (v1.0.87).** Hij stond op twee plekken: `ManagementSettings:Timezone`
en `Settings.TimeZone`. `TimeZoneService` startte op de config-waarde en schakelde pas naar de
DB-waarde zodra `SettingsService.SettingsChanged` vuurde — maar dat is een hosted service, dus ná het
migratieblok in `Program.cs`, en dáár staan twee tijdstempels: de back-up vóór een migratie
(`DbHelper.cs:77-80`) en het `AppVersions`-stempel. Bij verschillende waarden schreven die rijen
**elke start** in de verkeerde zone. Nu: `SettingsConfig.Timezone` weg, `TimeZoneService` heeft een
parameterloze constructor met `DefaultTimeZone`, en `Program.cs` leest de zone vóór de back-up uit
het bestand via `SqliteSetup.TryReadTimeZone` — **rauwe SQL, geen EF**, want dit draait vóór
`Migrate()` en het schema kan ouder zijn of de tabel kan ontbreken; faalt hij, dan blijft de default
staan. Bijvangst uit de tests: migratie `AddSettingsTable` **insert de Settings-rij zelf**, dus na
`Migrate()` is er altijd een zone; `SettingsService.EnsureDefaultsSeededAsync` vuurt alleen op een
echt lege tabel. Tests: `StartupTimeZoneTests` (4, tegen een echt SQLite-bestand). Totaal 302.

**`IOptions` bevriest, `IOptionsMonitor` niet (v1.0.86).** `EnergyStatisticsService` nam
`IOptions<HeatPumpConfig>` en `IOptions<PowerSystemsConfig>`. `IOptions<T>` is een singleton met een
waarde die bij de eerste resolutie wordt vastgezet — óók voor nieuwe scopes — dus een sectie die je
tijdens bedrijf toevoegt of weghaalt bereikte elke andere dienst wél en de statistiek niet, terwijl
het daar stilletjes de terugverdientijd verandert. Nu monitor + `OnChange`, met drie dingen die niet
vanzelf goed gaan: de seizoenscache (6 u) wordt geleegd, want die schaalt op de paneelconfiguratie;
`OnChange` vuurt twee keer per save (filewatcher), dus een debounce van 2 s; en de service is
`AddScoped` — in Blazor Server leeft een scope zolang het circuit — dus de abonnementen moeten weg
via `IDisposable`, anders houdt elke gesloten tab een dood object in leven. De pagina luistert op
`ConfigurationChanged` en herbouwt met de periode die hij toont. Meegenomen in dezelfde sweep, zelfde
patroon: `ConfigurationCheckService`, `ConfigurationService`, `SessyService` (bevroren
batterij-endpoints terwijl `BatteryContainer` zijn lijst wél herbouwt), `SolarService`,
`SolarEdgeInverterService` en `WeatherService`. Bewust *niet*: `TimeZoneService`, `SettingsService`
en `DbHelper` — die lezen bootstrap-waarden (tijdzone, connection string, backupmap) die pas bij een
herstart betekenis hebben.

## Gebouwd 10-08 (v1.0.94) — openstaand punt 0 opgelost, en de diagnoseketen die het opleverde

**De laadtaper hongerde de batterij uit.** Klacht: 's avonds blijft er lading staan terwijl de piek
€0,33-0,38 betaalt. Vier verkeerde diagnoses later (reserve, replacement cost, discount, horizon —
allemaal aantoonbaar níet de oorzaak) bleek de enige werkbare route: **de echte planner op de echte
invoer draaien**. Daarvoor is `SolveInputRecorder` gebouwd (v1.0.91, achter
`SESSY_RECORD_SOLVE_INPUTS`): schrijft `pricePoints`, `spec`, `opt` en `socBounds` per rebuild als
JSON weg. De replay reproduceerde productie tot op de cent (objective 2,0249 = 2,0249), en pas dáár
was bisectie zinvol.

Uitkomst: de taper bindt. Hij voorspelde **2,3 kW bij 80% SOC** waar de metingen ~5,3 kW laten zien,
omdat hij op een ratio (realized/requested) gefit wordt en dus alleen kwartieren met een
`PlannedUnthrottledPowerW` kan gebruiken — 223 van 7135, allemaal uit één hittegolf. De planner
geloofde dat, plande een trager laadvenster, en verkocht 6,61 kWh in plaats van 14,20.

**Oplossing: een bodem in watt, geen betere fit.** `ChargeCapabilityFloor` — per SOC-bin van 5% het
P90 van de gemeten laadvermogens × 0,9, geklemd op nameplate, uit *elk* laadkwartier (geen noemer
nodig, dus 917 in plaats van 223 samples; vreemde kwartieren eruit). `taperedChargeKWh` neemt
`max(taper, bodem)`. Waarom een bodem geldig is waar een fit dat niet is: een hoge meting bewíjst dat
de bank het kan, een lage bewijst niets (dat kan traag zonladen of een klein verzoek zijn) — precies
waarom "fit op watt" verworpen werd (analyse verderop in deze sectie). Het haalt de taper ook uit
zijn eigen lus:
een onderdrukt verzoek levert lage metingen die een nóg lagere taper fitten, maar over het
730-daagse venster onthoudt een percentiel van de bovenkant de goede samples die een gemiddelde
vergeet. Gemeten resultaat op de opgenomen invoer: **6,61 → 14,19 kWh**, eind-SOC 8,30 → 0,47, tegen
14,20 als je de taper hélemaal uitzet. De bodem haalt dus vrijwel het volledige gat dicht mét behoud
van het model.

**Diagnose die blijft staan.** `BatteryGreedyPlanner.Solve` heeft nu een optionele `trace`-callback
(null = geen enkele extra berekening) die per kwartier meldt waaróm er niet verkocht is: waarde
tegen bodem, slack, headroom. Daarmee kwam er meteen een tweede bevinding boven die niet van de
taper is: op de oude invoer blijven **16 kwartieren over die de planner zélf als winstgevend scoort
(tot €0,042/kWh) met 7,7 kWh slack en ~1 kWh headroom, en die hij toch niet neemt.** Dat is
openstaand punt 4 (greedy laat geld liggen), nu meetbaar in plaats van vermoed. Met de bodem erbij
is dat aantal 0 — het probleem verdwijnt in dit geval, maar de zwakte zit er nog.

Tips & Checks meldt nu wat de planner over laden gelooft (taper, gemeten bodem, gedekte SOC-bins) en
waarschuwt als die twee materieel uiteenlopen. Tests: `ChargeCapabilityFloorTests` (7),
`SolveInputRecorderTests` (2). Totaal 315.

**Les:** vier diagnoses op rij zaten ernaast omdat ze op gereconstrueerde invoer rustten. Twee keer
was de reconstructie zelf fout (de eenheden van `SolarForecastW` versus `ConsumptionForecastW`
verschillen). Bij planner-onderzoek: eerst de invoer opnemen, dan pas redeneren.

**Waarom de fit zelf niet gerepareerd is — analyse van 08-08, bewaard.** Dit stond tot v1.0.96 als
openstaand punt 0; het is de onderbouwing bij de bodem hierboven en bij wat er nog open staat.
Hoogst gemeten laadvermogen per SOC-band (`QuarterlyMeasurements`, vanaf 15-06): 10-20% → 5310 W,
20-30% → 5326, 30-40% → 5263, 40-50% → 5294, 50-60% → 5318, 60-70% → 5179, 70-80% → 5335,
80-90% → 5118, 90-100% → 4316. Dus ~0,80 van de 6600 W nameplate over vrijwel het hele bereik,
knik pas boven ~90% — terwijl de envelope-fit op 50% SOC 0,50 zegt (3281 W).
De envelope (v1.0.38) is niet de ontbrekende stap; die zit er al in. Het probleem is de dekking:
slechts **223 van 7135** `PlannedQuarters`-rijen hebben `PlannedUnthrottledPowerW ≠ 0`, allemaal
vanaf 28-07. Het fitvenster beslaat temp 20,2-32,4 °C en t48 17,6-25,7 °C, tegen 1,6-36,0 °C in de
historie. Daardoor is `temp-20` in de envelope-fit insignificant (t = −0,54) en wordt **álle**
derating op SOC geladen (B = 0,568). Alle 28 kwartieren boven 4800 W komen uit juni bij 15-16 °C —
en hebben stuk voor stuk `PlannedUnthrottledPowerW = 0`, dus ze zijn allemaal uitgesloten.
Voorbeeld 05-06: 5318 W bij 57% SOC, 5179 bij 65%, 5066 bij 72%, 4944 bij 79%; 15-06: 5335 W bij
72%, 5118 bij 86%.
**Uitgesloten oplossing:** "fit op watt in plaats van ratio" (zoals op de ontlaadkant, v1.0.72).
Nagerekend in elke binning — strikte max per (SOC × temp)-bin, banden, 10/20 SOC-bins — komt de
SOC-helling er *positief* uit (+430 tot +866 W over het volle bereik), fysiek onmogelijk. Op de
laadkant zijn lage-SOC-kwartieren overwegend traag zonladen; dat meet vraag, geen kunnen. Op de
ontlaadkant speelt dat niet, daar werkt het wél. Dat is precies waarom de bodem een percentiel van
de bovenkant is en geen fit: een hoge meting bewíjst wat de bank kan, een lage bewijst niets.
**Wat de fit wel nodig heeft:** maanden `PlannedUnthrottledPowerW`-dekking over een breed
temperatuurbereik, zodat C en D echt te scheiden zijn van B. Tot die tijd niets forceren — een
tweede filter-tweak lost het niet op.

## Gebouwd 10-08 (v1.0.79 / v1.0.81 / v1.0.82 / v1.0.83) — losse fixes

**UI laat weg wat je niet hebt (v1.0.83).** Zonder zonnepanelen heeft de Solar-pagina geen inhoud en
staan er in Statistics vijf kaarten die alleen nul kunnen zijn. Eén definitie: `PowerSystemsConfig.HasSolar`
(minstens één omvormer geconfigureerd), uitgeserveerd door `SystemCapabilitiesService` (singleton, leest
elke keer `CurrentValue` zodat een config-wijziging zonder herstart aankomt) en als
`PageBase.HasSolar` / `DashboardStatistics.SolarIsConfigured`. Verborgen: het menu-item Solar power,
en in Statistics "Solar production", "Self-sufficiency", "Self-consumption", "Performance ratio",
"Avg / peak daily solar" plus de "self sufficiency"-kaart in Energy Flows en de zinsnede "Solar and
battery are treated as one integrated system". *Self-sufficiency* gaat mee omdat het
`(verbruik − netinvoer)/verbruik` is: zonder zon komt alles uiteindelijk van het net en klemt het op 0.
De Solar-pagina zelf toont een uitleg in plaats van grafieken (een bladwijzer komt er nog steeds op uit)
en slaat het laden van metingen over. Precedent gevolgd van `HeatPumpIsConfigured`, dat dit al zo deed.
Tests: +1 in `NoSolarConfigurationTests` (9). Totaal 298.

**`secrets.json` verzon batterijen (v1.0.82).** Lokaal bleven er drie batterijen gevonden worden
nadat `appsettings.json` naar één was gestript, nu met `ArgumentNullException (Parameter 'BaseUrl')`
in plaats van een timeout. Niet de template — het log meldde netjes "Ignoring the built-in
appsettings.json". `SessyWeb/secrets.json` droeg nog `Batteries: 1, 2, 3` met alleen
`UserId`/`Password`, en die wordt ná appsettings toegevoegd, dus de sleutels "2" en "3" ontstonden
daar. Dezelfde klasse als v1.0.78, één laag dieper: **secrets vullen een gedeclareerd apparaat aan,
ze declareren er geen.** Nu `SessyBatteryEndpoint.IsConfigured` / `SessyP1Endpoint.IsConfigured`
(= `BaseUrl` gevuld); `SessyBatteryConfig.ConfiguredBatteries` draagt alle `Total*`-sommen zodat een
restant geen capaciteit toevoegt, en `BatteryContainer` slaat zulke entries over mét een
Warning-regel die de oorzaak noemt — stil overslaan zou de volgende keer weer een halve dag zoeken
kosten. `P1MeterContainer` idem. Tests: +2 in `NoSolarConfigurationTests` (8). Totaal 297.

**Dode `ChargedInControl` uit `ManagementSettings` (v1.0.81).** Stond in de template maar bond
nergens op: `SettingsConfig` kent alleen `Timezone` en `DatabaseBackupDirectory`. De echte vlag is
`Settings.ChargedInControl` in de DB-rij — checkbox op de Settings-pagina, gelezen door
`ControlModeService`. Regel verwijderd; README noemde hem al niet.

**Vergelijkingstoggle werkt nu beide kanten op (v1.0.79).** `ShowCharged` toonde alleen Charged;
onder Charged stond de checkbox uit dan het *uitvoerende* plan niet op het scherm en het onze wél,
als vlakken. Nu heet de vlag `ShowOther` en voegt hij altijd degene toe die **niet** uitvoert:
label "Show Charged" als wij sturen, "Show SessyWeb" als Charged stuurt (`OtherName`). Vier afgeleide
properties dragen de zichtbaarheid, zodat de markup geen samengestelde condities meer bevat:
`ChargedHasTheAreas` (= `ChargedInControl && HasChargedSchedule`), `WeHaveTheAreas`,
`ShowChargedShadow`, `ShowOurShadow`. De uitzondering die eerder het hele gedrag stuurde blijft
bestaan als precies dát: stuurt Charged maar kwam er geen schema binnen, dan houdt ons plan de
vlakken — anders staat er niets. Beide SOC-lijnen kregen een `Visible` (die van ons had er geen),
dus er staat nooit meer één rode doorgetrokken SOC-lijn te veel op de grafiek.

## Gebouwd 10-08 (v1.0.95 / v1.0.96) — issue #4: Consumption blijft leeg

Melding van buiten (GitHub issue #4, HindrikDeelstra): "Het menu Consumption laadt wel een site,
zonder foutmeldingen, maar ook zonder data. **Geen logs om te plakken.**" Die laatste zin is het
bewijsmateriaal, niet een gebrek eraan: het productie-logniveau staat op Warning, dus een pad dat
níets meldt is een pad dat helemaal niet logt. Dat sluit meteen de voor de hand liggende diagnose
uit — een falende weer-fetch spamt elke seconde `An error occurred while monitoring p1 meters.`

**Vier faalpaden gevonden, drie ervan volledig stil.**
1. **Weer was een harde afhankelijkheid.** `EnsureServicesAreInitialized` **gooide**
   `InvalidOperationException("Weather service not initialized")`, en stond vóór de opslaglus in
   `Process()`. `WeatherService._initialized` wordt alleen `true` na een geslaagde HTTP-fetch, en
   `WeatherExpectancyConfig.BaseUrl` heeft `string.Empty` als default → `new Uri("")` gooit →
   nooit initialized. Ontbrekende `WeerOnline`-sectie = nul consumption-rijen, voor altijd. Dit is
   dezelfde klasse als v1.0.78/80/82: sinds de ingebakken `appsettings.json` niet meer meegaat is
   "sectie afwezig" een echt geval, en dit was de laatste dienst die daar nog op stukliep.
   Nu `WaitForWeatherAsync` (geeft `bool`, gooit niet). De 10-seconden wachtlus draait **alleen op
   de eerste cyclus** — een opstart-gratie; daarna zou hij de 1-seconde-sampling stallen zolang de
   feed weg is. Ontbrekend weer logt één regel op de overgang, niet elke seconde. Opslaan gaat door
   met de `-999`-sentinels die `Consumption.Humidity/Temperature/GlobalRadiation` al kenden.
2. **Zonder weer plande de planner op 0 W verbruik.** De maandprofiel-terugval in
   `CalculateEstimatedConsumptionForAQuarterHour` stond *binnen* de `if (currentWeather != null)`-tak:
   precies in het geval waarvoor hij bestaat was hij onbereikbaar, en bleef
   `EstimatedConsumptionPerQuarterInWatts` op 0. Zonder de fix onder punt 1 was dit onbereikbaar
   (het gooide eerder), dus het is er samen mee opgelost, niet los.
3. **Een mislukte term werd als 0,0 opgeslagen.** `CalculateConsumption` gaf bij élke exception
   `0.0` terug; die samples gingen mee in het kwartiergemiddelde. Consumption = zon + net + batterij,
   dus één onbereikbare batterij die 3 kW ontlaadt maakt het kwartier precies 3 kW te laag — stil
   fout opgeslagen data, en dat voedt de verbruiksvoorspelling. Waren *alle* samples 0, dan verdween
   het kwartier via de tak `"Consumption is negative 0. Cleared data"`. Nu `Task<double?>` met `null`
   bij een kapotte term, en `StoreConsumption` slaat zo'n sample over. Bewust géén partiële som.
4. **Lege metersectie was volledig stil.** `Process()` liep `foreach` over een lege `P1Meters` en
   logde niets — het pad dat het beste bij "geen logs om te plakken" past. Nu één Warning-regel.

**Tips & Checks dekt nu de invoerkant (v1.0.95).** `ConfigurationCheckService` controleerde de
warmtepomp en (indirect) de zon, maar niets van wat consumption voedt. Vier checks erbij: weer
(`WeerOnline`), P1 (`Sessy:Meters`), batterijen (`Sessy:Batteries`) en de consumption-historie zelf
(leeg / laatste rij > 1 u oud / < 72 van 96 kwartieren in 24 u). De config-checks tellen
gedeclareerd tegen `IsConfigured` en noemen de overgeslagen sleutels bij naam, want de
secrets.json-restanten uit v1.0.82 zijn de terugkerende oorzaak. Historie via `ServiceBase.Query`
met `Max`/`Count`, zodat het SQL-side blijft. Alle nieuwe config wordt via `IOptionsMonitor` gelezen
(v1.0.86), niet `IOptions`.

Bijvangst opgeruimd in hetzelfde pad: een `GetDetails`-aanroep per kwartier waarvan het resultaat
nooit werd gebruikt (een HTTP-rondje voor niets) en een ongebruikte `var test = nextQuarter`.

**Niet getest.** `ConsumptionMonitorService` hangt aan `P1MeterService`, `SolarInverterManager` en
`BatteryContainer` — concrete klassen zonder interface, dus niet te mocken zonder refactor.
`CalculateConsumption` en `WaitForWeatherAsync` zijn de testwaardige stukken zodra die drie achter
een klein interface zitten. Staat als openstaand punt 6.

**Les:** "geen logs" is een aanwijzing, geen dood spoor. Op een Warning-logniveau selecteert het
precies de takken die niets zeggen — hier de lege meterlijst — en verwerpt het de takken die wél
zouden schreeuwen. Zoek bij zo'n melding eerst naar de stille paden.

## Gebouwd 11-08 (v1.0.101) — health-check verklaarde de nacht tot storing

Eerste echte draai met de Sessy-bron, om 22:21: Tips & Checks meldde *"Inverter configured but
unreachable"*. Config was goed; de melding was fout, en hij legt openstaand punt 8 meteen bloot.

**Waarom hij nu pas vuurt.** `SunspecInverterService.Start` draait zijn lus inline, dus
`SolarInverterManager.ExecuteAsync` kwam nooit voorbij de `foreach` aan `RunHealthCheckLoopAsync` —
punt 8. `SessyInverterService.Start` keert wél meteen terug (`Task.Run`), dus met de Sessy-bron draait
de health-check voor het eerst in de geschiedenis van dit project.

**En dan blijkt hij stuk.** `CheckAvailabilityAsync` toetst `Now - LastSuccessfulReadUtc < 60s`, maar
élke bron stopt met lezen buiten daglicht (`Process()` gaat dan vroeg terug). Na zonsondergang
veroudert die stempel dus vanzelf en wordt de bron na 30 s offline verklaard — 's ochtends weer
online. Bij `LastSuccessfulReadUtc = DateTime.MinValue` (nog nooit gelezen) is het meteen raak.
Nu: buiten daglicht wordt er niets beoordeeld. Een verouderde stempel bewijst duisternis, geen
storing.

Twee dingen die hieruit volgen:
- **De melding noemde het verkeerde apparaat.** "Inverter" terwijl er geen omvormer in de opstelling
  zit stuurt de lezer hardware controleren die er niet is. De tekst kiest nu op
  `ActiveInverterServices` welke bron hij noemt.
- **`TimeZoneService.GetSunlightLevel` is `virtual`** geworden, zelfde reden als `Now`: anders is dit
  alleen met de echte klok te testen en dus tijdsafhankelijk flaky. `CheckAvailabilityAsync` is
  `public` — precedent `SumRenewablePhases`/`ClampToCapacity`/`ChargeCostBasisService.Project`.

Let op de asymmetrie die blijft staan: met een Modbus-bron draait de health-check nog steeds niet
(punt 8 is niet opgelost), met de Sessy-bron wel. Tests: 2 erbij in `SessySolarTests` (20, de nacht-
en de daglichtkant). Totaal 340.

## Gebouwd 11-08 (v1.0.100) — welke Sessy's de zon meten

`Endpoint.Batteries` (`List<string>?`, optioneel): welke batterijen de CT-klemmen dragen, met de
sleutels uit `Sessy:Batteries:Batteries`. **Geen adressen** — die blijven op één plek staan.
De defaultsituatie verandert niet: afwezig of leeg = alle batterijen optellen, wat klopt zolang
alleen één Sessy klemmen heeft (een Sessy zonder klemmen rapporteert 0 W). Het veld is er voor het
enige geval waarin optellen fout is: meerdere Sessy's die dezelfde klemmen zien. De `InverterMaxCapacity`-
klem betrapt dat alleen dicht bij piek (zie v1.0.99, punt 2), dus dit is de expliciete uitweg.

Twee beslissingen die niet vanzelf goed gaan:
1. **Leeg ≠ niets.** Een afwezig of leeg filter betekent *alle* batterijen; een filter dat op niets
   matcht betekent *geen enkele*. `SelectBatteryIds` (statisch, puur, `out HashSet<string>? selectedIds`)
   drukt dat verschil uit met `null` tegen een lege set. Vier tests dekken de vier gevallen.
2. **Een typefout valt niet terug op "alles".** Matcht het filter niets, dan leest de bron niets en
   zet hij `IsAvailable = false` — dus de consumption-sample wordt overgeslagen (v1.0.98) in plaats
   van als 0 W opgeslagen. Terugvallen op alle batterijen zou precies de reden negeren waarom iemand
   een subset opschrijft. Onbekende sleutels staan bij naam in Tips & Checks; stil overslaan is de
   fout van v1.0.82 die een halve dag zoeken kostte.

Tests: 5 erbij in `SessySolarTests` (18). Totaal 338.

## Gebouwd 11-08 (v1.0.99) — zon meten via de Sessy's

Vervolg op v1.0.98: dat verklaarde waarom consumption overdag leegblijft (geen PV-bron), maar loste
niets op. De Sessy meet zelf: `PowerStatus.RenewableEnergyPhase1/2/3` (`SessyService.cs:282-295`),
CT-klemmen om de PV-groep. Dat veld bestond al en werd **alleen getoond**
(`BatteryInfoComponent.razor:73-79`), nergens gebruikt.

`SessyInverterService : ISolarInverterService` maakt er een volwaardige bron van. De interface was de
juiste plek: consumption, `HardwareStatusService`, de statistiek, SolarPowerPage en de
performance-factor hoeven geen van alle te weten waar het getal vandaan komt.

**Configuratie is de provider-key `"Sessy"` binnen `PowerSystems:Endpoints`**, niet een nieuwe sectie.
Dat erft `HasSolar` (de UI-gating van v1.0.83), de `SolarPanels`-geometrie die `SolarService` voor de
forecast nodig heeft — die is nodig ongeacht hóé je meet — `InverterMeasurements` per provider, en
`InverterMaxCapacity` als cap. `IpAddress`/`Port`/`SlaveId` blijven leeg.

**Er komt geen IP-adres bij.** De bron leest `BatteryContainer.Batteries`, dus de adressen en
credentials uit `Sessy:Batteries` — een tweede plek zou een tweede waarheid zijn. Hij neemt ze
**allemaal** en telt op. Let op de valkuil in de configvorm: de binnenste sleutel (`"1"`) onder
`Endpoints:Sessy` is **geen batterijnummer** maar alleen het label dat als `InverterId` in
`InverterMeasurements` belandt. Dat het er precies uitziet als een batterij-id is een neveneffect van
het hergebruiken van `Endpoint`; het is bij het eerste lezen al misgegaan. Aanwijzen wélke Sessy kan
sinds v1.0.100 met `Endpoint.Batteries` — zie hieronder.

Vier dingen die niet vanzelf goed gaan:
1. **Optellen over batterijen mag, dankzij een meting.** Om 21:38 (donker) rapporteerde batterij 1
   stroom op de renewable-CT's (166/119/144 mA) en batterij 2 en 3 exact 0 mA — spanning meten alle
   drie, die komt van de netaansluiting. Een Sessy zónder klemmen draagt dus exact 0 bij en de som
   klopt. **Nog te bevestigen bij daglicht**; tot dan is dit een aanwijzing, geen bewijs.
2. **De cap is eenzijdig bewijs.** `InverterMaxCapacity` klemt de som en telt de overschrijdingen
   (`ClampToCapacity`, statisch en puur). Boven de cap bewíjst dubbeltelling — fysiek onmogelijk —
   maar eronder bewijst niets: drie batterijen die elk een echte 1500 W zien halen 4500 W en blijven
   onder 5000. Daarom klemmen én melden, en de cap níet gebruiken om de combinatieregel te kiezen.
   Vastgelegd in `Double_counting_below_the_cap_is_not_detected`.
3. **Curtailment zit volledig aan de Modbus-kant** — `InverterCurtailmentService` →
   `ThrottleInverterToWatts` → `ThrottleInverterToPercentage`. Nieuw:
   `ISolarInverterService.SupportsCurtailment` → `SolarInverterManager.CurtailmentIsPossible` →
   `HardwareStatusService` → `EnergySystemInput` → een terugval bovenaan `EvaluateCurtailment`.
   Niet alleen een gemiste throttle: **FORCE_CHARGE laadt vol uit het net omdát het aanneemt dat de
   omvormer op 0 W staat** (`EnergySystemStateMachine.cs:127-135`). Bewust een **capaciteits**-check
   en geen bereikbaarheidscheck, zodat een offline Modbus-omvormer exact het oude gedrag houdt —
   `PriceNegative_CurtailmentPossible_ButInverterOffline_KeepsTheOldFallback` legt dat vast.
   `ThrottleInverterToWatts` keert vroeg terug vóór `LastSetpointW` wordt gezet, anders toont de UI
   een throttlepercentage voor een opdracht die nooit verstuurd is.
4. **Óf-óf, niet allebei.** `FillActiveInverterServices` laat bij `"Sessy"` + een Modbus-provider
   alleen Sessy staan, met één Warning die beide noemt. Niet gooien — config wijzigt tijdens bedrijf
   (`OnChange`, v1.0.86).

`StoreData` is uit `SunspecInverterService` getrokken naar `InverterMeasurementWriter`
(`Services/Items/`, gewone klasse, geen DI). Zonder dat schrijft de Sessy-bron wél een goed live
getal en een lege historie: SolarPowerPage, de statistiek en
`SolarService.CalculateHistoricalPerformanceFactorAsync` lezen allemaal die tabel.

Tips & Checks: beide bronnen tegelijk (Error), geen curtailment zolang Sessy de bron is (Warning),
een uur daglicht op 0 W (Error — klemmen zitten niet om de PV-groep; **consecutief** geteld, want
dageraad en schemer lezen legitiem nul), en cap-overschrijdingen (Error).

**Nog niet gemeten, en dat is de kern van wat open staat:** `renewable_energy_phase*` wordt nergens
bewaard, dus er is geen historie om achteraf op te toetsen — alleen live bemonsteren. Deze
installatie heeft *beide* bronnen, dus de ijking is de Sessy-som per kwartier naast de
SolarEdge-waarde in `InverterMeasurements`. Zie openstaand punt 9. Tests: `SessySolarTests` (13) en
5 nieuwe in `EnergySystemStateMachineTests`. Totaal 333.

Bijvangst, niet aangeraakt: `SmaInverterService` en `SolisInverterService` staan **niet** in
`Program.cs` (alleen SolarEdge, Enphase, GoodWe, Huawei, Sungrow, Victron). Wie `"SMA"` of `"Solis"`
configureert krijgt stil niets — `FillActiveInverterServices` filtert op wat DI aanlevert, dus die
provider bestaat simpelweg niet. Zelfde klasse als issue #4: geen fout, geen log, geen data.

## Gebouwd 11-08 (v1.0.98) — issue #4 heropend: consumption alleen bij netto import

Melder (HindrikDeelstra) na v1.0.96/97: consumption-rijen bestaan **exact** in het venster waarin er
geen netto export is (20:30-06:45, plus één losse meting om 17:45 tijdens het koken). Zijn conclusie
— "iets met een negatief getal uit de P1" — klopt, maar de oorzaak zit een stap eerder.

`ConsumptionMonitorService.CalculateConsumption` rekent `solar + net + battery`. Zijn PV zit **niet**
in SessyWeb (geen `PowerSystems`-sectie; "PV via Sessy" is nog onderzoek), dus
`SolarInverterManager.GetTotalACPowerInWatts()` geeft 0 — geen exception, geen log. Zodra het huis
exporteert is `net` negatief en groter dan de rest, komt de som onder nul, en gooit
`StoreConsumption` het kwartier weg via de guard `averageConsumptionWh > 0`. 's Nachts is PV echt 0
en klopt de som wél. Dat is precies het waargenomen patroon.
**Dit is geen rekenfout maar een ontbrekende meting**: zonder PV-bron is het verbruik overdag
onmeetbaar. Erger dan de lege rijen zijn de rijen die het wél haalden: bij netto import mét
zonproductie wordt het kwartier **te laag** opgeslagen, en dat voedt de verbruiksvoorspelling. Daarom
niet geklemd op 0 en niet alsnog opgeslagen — dat zou opzettelijk foute historie schrijven, dezelfde
afweging als de `null`-in-plaats-van-partiële-som van v1.0.96.

Wat er wél gerepareerd is, dezelfde klasse maar bij mij thuis óók actief:
1. **Een onbereikbare omvormer levert stil 0 W.** `SunspecInverterService.GetTotalACPowerInWatts`
   geeft `0.0` bij `!IsAvailable` (regel 216) en de manager telt dat op. In `CalculateConsumption`
   was dat niet te onderscheiden van duisternis, dus het verbruik werd stilzwijgend de hele
   zonproductie te laag opgeslagen — geen exception om te vangen. Nieuw:
   `SolarInverterManager.SolarIsMeasurable` (= `AllAvailable`, of geen daglicht). `AllAvailable` en
   niet `IsAvailable`, want dit schraagt een som: één ontbrekende omvormer is al genoeg. Nul
   omvormers is een huis zonder panelen en blijft meetbaar (`All` op een lege lijst is `true`).
   Onmeetbaar → `error = true` → sample overgeslagen, met één logregel op de overgang (de lus tikt
   elke seconde). `IsAvailable` staat op de services default `true`, dus geen gat bij het opstarten.
2. **De weggooi-regel noemt de oorzaak.** Was `Consumption is negative {x}. Cleared data`; noemt nu
   het onderscheid tussen "geen omvormer geconfigureerd" en "alle drie gelezen maar tellen niet op".
3. **Tips & Checks: `CheckSolarMeasurement`.** `ConsumptionMonitorService` telt de weggegooide
   kwartieren (`NegativeConsumptionQuarters` / `LastNegativeConsumptionAt`) — dat is het enige
   directe bewijs dat er een term ontbreekt, en op log-niveau Warning ziet de melder het wel maar
   staat het niet in de UI. Drie takken: geen zon geconfigureerd + weggegooide kwartieren = Error
   met de volledige uitleg; zon geconfigureerd maar onbereikbaar = Warning; alles gelezen en tóch
   negatief = Warning die naar een niet-geconfigureerd apparaat wijst.

Bewust niet gedaan: een check op basis van gemeten export uit `EnergyHistory`. De teller is directer
bewijs (hij telt precies de kwartieren die de gebruiker mist) en kost geen query.
Niet getest — `ConsumptionMonitorService` is nog steeds niet mockbaar, openstaand punt 6. Totaal
blijft 315.

## Gebouwd 11-08 (v1.0.97) — issue #2: menu klapt bij elke klik dicht

Melding van buiten (GitHub issue #2, HindrikDeelstra): het menu uitklappen met het icoon boven het
menu houdt geen stand, elke volgende menuklik vouwt het weer terug naar iconen. Cosmetisch, en
precies één regel: elk `RadzenPanelMenuItem` in `MainLayout.razor` draagt `Click="@CollapseMenu"`,
en die methode zette `DisplayStyle` onvoorwaardelijk terug op `Icon`.

Niet zomaar weggehaald maar instelbaar gemaakt, want op een telefoon dekt het uitgeklapte menu
(200 px tegen 50 px) een deel van de pagina af — dichtklappen ná de klik is daar het betere gedrag.
`Settings.KeepMenuExpanded` (migratie `AddKeepMenuExpanded`), default **aan**. `CollapseMenu` keert
vroeg terug als de vlag aan staat; verder is er niets aan de menulogica veranderd.
Het staat op een eigen tab **User interface** op de Settings-pagina — de eerste van wat er verder
aan schermvoorkeuren komt, gescheiden van de operationele tuning op "Management Settings". De tab
bindt aan dezelfde `_settings` en hergebruikt `SaveSettingsAsync`.

Drie dingen die niet vanzelf goed gaan:
1. **De migratie is met de hand op `defaultValue: true` gezet**; EF scaffoldt `false` en de
   C#-initializer is geen DB-default. Zonder die aanpassing zou een bestaande installatie na de
   upgrade stil het oude gedrag houden terwijl een verse DB het nieuwe krijgt.
2. **`MainLayout` implementeerde `IDisposable` niet.** Er stónd een `Dispose()` met
   `ResizeListener.OnResized -= …`, maar zonder `@implements IDisposable` roept Blazor die nooit
   aan. Voor de resize-listener was dat een lek per circuit; met een abonnement op de **singleton**
   `SettingsService` erbij zou elk gesloten circuit blijven leven. `@implements IDisposable`
   toegevoegd — dat repareert meteen het bestaande lek.
3. **`SettingsChanged` vuurt buiten het circuit**, dus de handler gaat via `InvokeAsync`. Zo komt
   een wijziging zonder herladen aan, net als bij de andere `IOptionsMonitor`-abonnementen (v1.0.86).

Niet getest: `MainLayout` valt onder openstaand punt 5 (geen projectreferentie van `SessyUnitTests`
naar `SessyWeb`). Totaal blijft 315.

## Openstaande punten
*De nummering heeft een gat (2 is vervallen). Niet hernummeren — elders in dit document wordt naar
"Openstaande punten 4" verwezen.*

0. **De taper-fit zelf blijft scheef.** De gemeten bodem uit v1.0.94 dekt het praktische probleem af,
   maar de fit herstelt pas met maanden `PlannedUnthrottledPowerW`-dekking over een breed
   temperatuurbereik; Tips & Checks laat zien hoe ver hij ernaast zit. De volledige analyse en de
   verworpen oplossing ("fit op watt") staan bij v1.0.94 — niets forceren tot die dekking er is.
1. **Productie-verificatie van v1.0.72 t/m v1.0.75 staat nog open** — zie de checklist onderaan.
   De ontlaadkant, de NaN-fix en de gasprijs-fix zijn alleen lokaal getoetst.
3. **De discount is nooit eerlijk getoetst.** `FutureValueDiscountPerHour` hedget voorspelfouten,
   maar elke replay tot nu toe rekent af tegen dezelfde forecast waarmee gepland is. Een harnas dat
   afrekent tegen gemeten verbruik en zon (`QuarterlyMeasurements` / `EnergyHistory`) zou de vraag
   wél beantwoorden — en meteen die van `PredictedPriceMode` en de nachtreserve.
4. **Greedy laat geld liggen, hoeveel is onbekend.** Een langere horizon levert minder op (zie
   v1.0.77), wat alleen bij een suboptimale heuristiek kan. Een DP over (kwartier, SOC-niveau) geeft
   het optimum onder dezelfde constraints en is naar schatting sneller dan de huidige zoektocht;
   het is wel een herbouw van de kern.
5. **`SessyWeb.Helpers.ScreenInfo` is niet getest** omdat `SessyUnitTests` geen projectreferentie
   naar `SessyWeb` heeft. Sinds v1.0.76 draagt `Update` een beslissing (wel/niet hertekenen), dus
   dat is nu testwaardig. Twee wegen: `ScreenInfo` verhuizen naar `SessyCommon` (het is een pure
   helper zonder Blazor-afhankelijkheden, en `PageBase`/`BaseComponent`/`_Imports` moeten dan mee),
   of `SessyWeb` als projectreferentie toevoegen aan de testset. Nog niet gekozen.
6. **`ConsumptionMonitorService` is niet getest** (v1.0.96). Hangt aan `P1MeterService`,
   `SolarInverterManager` en `BatteryContainer` — concrete klassen zonder interface, dus niet te
   mocken. `CalculateConsumption` (partiële som → `null`) en `WaitForWeatherAsync` (opstart-gratie,
   daarna niet blokkeren) zijn de stukken die een test verdienen zodra die drie een interface
   krijgen. Zelfde patroon als `ChargeCostBasisService.Project` en `ChargedScheduleService.ToQuarters`:
   de pure kern eruit trekken, dan pas testen.
7. **Issue #4: de Sessy-bron is gebouwd (v1.0.99) maar bij de melder niet bevestigd.** Vraag hem de
   provider-key `"Sessy"` te configureren en dan Tips & Checks; die noemt alle faalgevallen bij naam.
   Werkt het niet, dan is de vraag of zijn Sessy's CT-klemmen om de PV-groep hebben — zonder die
   bedrading meet ook de Sessy niets en is er geen tweede weg (de P1-respues `P1Details` heeft géén
   PV-veld, alleen `power_consumed`/`power_produced` van het net).
8. **`SolarInverterManager.ExecuteAsync` bereikt zijn health-check nooit.** `SunspecInverterService.Start`
   draait zijn eigen lus *inline* (regel 113-141, `while` binnen `Start`), en `ExecuteAsync` doet
   `foreach { await service.Start(...) }` gevolgd door `RunHealthCheckLoopAsync`. Met één omvormer
   geconfigureerd komt die tweede regel dus nooit aan de beurt. Gevolg: `CheckAvailabilityAsync` is de
   enige plek die `ISolarInverterService.IsAvailable` zet, dus die blijft eeuwig op zijn default
   `true`. Daarmee is **v1.0.98's `SolarIsMeasurable`-guard in de praktijk dood** voor Modbus-bronnen
   — de bedoelde bescherming (een onbereikbare omvormer levert stil 0 W) treedt niet op, en
   `SolarService.ApplyPerformanceFactor` en `InverterCurtailmentService` denken ook altijd dat de
   omvormer online is. `SessyInverterService.Start` keert wél meteen terug (`Task.Run`), dus met de
   Sessy-bron loopt de health-check wel. Fix is één regel, maar hij zet een keten aan die nog nooit
   gedraaid heeft (omvormers die ineens als offline gemarkeerd worden) — eerst meten wat
   `LastSuccessfulReadUtc` in productie doet, dan pas aanzetten. **Wat die keten doet is inmiddels
   deels bekend**: bij de Sessy-bron vuurde hij meteen vals (v1.0.101, de nacht als storing). Reken
   erop dat er meer boven komt zodra hij ook voor Modbus gaat draaien.
9. **De Sessy-zonmeting is nooit tegen een referentie gelegd.** `renewable_energy_phase*` wordt
   nergens bewaard, dus er valt niets retroactief te toetsen. Twee open vragen: (a) meet alleen
   batterij 1 (aanwijzing uit de 21:38-meting) of meten meerdere dezelfde klemmen, en (b) hoe
   verhoudt de Sessy-som zich tot de echte productie. Deze installatie heeft *beide* bronnen, dus de
   ijking is de Sessy-som per kwartier naast de SolarEdge-waarde in `InverterMeasurements` — één
   zonnige dag is genoeg. Doen vóórdat iemand anders erop gaat draaien.

## Diagnosed, niet-een-bug
- Setpoint requested ≠ Setpoint: Sessy-hardware klemt/tapert zelf (CC/CV, SOC-afhankelijk). API meldt geen reden. `Battery.SetpointRequested` (ons) vs `Sessy.PowerSetpoint` (device).
- 21-07 avond niet-ontladen: economisch correct (export tegen €0,30 was netto verlies vs. latere terugkoop); de niet-uitvoering die avond was de `j=1`-bug op v1.0.4.

**Verifiëren gaat niet via het log.** Het productie-log-niveau staat **bewust** op Warning
(`appsettings.json`, `Logging:LogLevel:Default`) en `LoggingService` is een kale passthrough. Alle
model-fitregels staan op `LogInformation` en verschijnen dus nooit: `Efficiency curve fitted…`,
`Charge taper fitted…`, `Discharge capability fitted…` (`StrategyMilpService`),
`Replacement cost …` (`ReplacementCostService`) en `Planner learning: …` — die laatste logt Warning
als een grens gepind wordt en Information als alles goed gaat, dus je ziet er alleen de storing van.
Niet ophogen; toets tegen de DB, dat is sterker bewijs want het is wat er werkelijk gepland en
gestuurd is.

**Checklist bij openstaand punt 1 — te verifiëren na de productie-run van v1.0.72 t/m v1.0.75:**
1. `PlannedQuarters` op ontlaadkwartieren: `PlannedUnthrottledPowerW` hoort **5100** te zijn (het
   echte verzoek) in plaats van `PlannedPowerW / 0,60`, en `PlannedPowerW` loopt tot ~4121 W in
   plaats van te blijven steken op ~3060 W. Onder 20% SOC zakt hij evenredig mee.
2. `QuarterlyMeasurements`: haalt de avondontlading hogere vermogens, worden de sessies korter en
   komt de eind-SOC lager uit?
3. Beweegt de ontlaadlijn in de throttle-grafiek? Die zat door de `plan / ratio`-reconstructie
   rekenkundig vast; elke waarde was een dekpunt.
4. Blijven `DbHelper: slow`, `ThreadPool busy` en "UI blocked: clock tick ran N ms late" stil?
   (Die staan wél op Warning en zijn dus zichtbaar.)
5. Geeft `GET /BatteryManagement/Inverter/AcPowerInWatts` weer een getal in plaats van een 500, ook
   als er een corrupte Modbus-scale-factor voorbijkomt? Zoek op
   "discarded a corrupt AC power reading" (Warning, dus zichtbaar).
6. Blijft de gasprijs op de warmtepomp-pagina op de laatst bekende waarde staan als de Enever-feed
   niets bruikbaars teruggeeft, in plaats van op belasting-alleen te springen?
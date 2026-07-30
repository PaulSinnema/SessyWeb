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
- **Bump the version on every change**: increment the patch in `SessyWeb/Shared/MainLayout.razor:9` (`string Version = "v1.0.x";`).
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

`DataService` classes derive from `ServiceBase<T>` (`Add`, `AddOrUpdate`, `Update`, `Remove`, `Get`, `GetList`) and go through `DbHelper`, which serializes **all** DB access behind a single process-wide `SemaphoreSlim` and creates a fresh scope + `ModelContext` per call. Consequences:

- Never hold a `ModelContext` across calls; never call a data service from inside another data service's callback (deadlock on the semaphore).
- Reads use `AsNoTracking()`, so returned entities are detached — write back through `AddOrUpdate`/`Update`.
- `AddOrUpdate`/`Update`/`Remove` require `T : IUpdatable<T>` and use that `Update()` implementation to copy fields.
- `ExecuteTransaction` throws if `SaveChangesAsync` writes 0 rows.

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
C#/.NET Blazor Server EMS. Stuurt 3× Sessy batterij (cap 16,2 kWh; raw charge 6600W/discharge 5100W, praktijk max ~4,4kW), SolarEdge inverter, Daikin warmtepomp. Draait op Synology NAS via Docker. Huidige versie **v1.0.22**. Locatie: Apeldoorn.

## Werkafspraken
- Antwoorden in het Nederlands, caveman-ultra kort. Code-commentaar in het Engels, **kort — één regel waar mogelijk** (user leest alle comments na ter controle).
- **Niet gissen**: eerst verifiëren in code/DB/web. Deze sessie ging herhaaldelijk mis door aannames — meerdere keren "gevonden!" geroepen en ernaast gezeten. Bij planner-analyse: DB-tabellen naast elkaar leggen en met de Python-simulator narekenen vóór conclusies.
- Code-behind boven `@code`-blokken. Radzen Blazor overal. Radzen API's verifiëren via `raw.githubusercontent.com/radzenhq/radzen-blazor/master/...` (rendering-pad én crosshair/tooltip-pad — `CartesianSeries.DataAt/TooltipY` unwrapt `double?` hard).
- Na codegeneratie altijd zelf reviewen op syntax/naamfouten/dubbele code/missing usings. Braces + code-parens balanceren (comments negeren bij paren-telling).
- **Versie ophogen bij ELKE wijziging** in `SessyWeb/Shared/MainLayout.razor`, variabele bovenaan: `string Version = "v1.0.16";` (enige plek). Patch-level ophogen (1.0.16 → 1.0.17).
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

## Openstaande punten
1. Verifiëren op productiedata: revenue mét en zonder `CarryForwardEnabled` op teruggespeelde dagen vergelijken (niet naar SOC kijken). Faalmodus: terminal value te hoog → batterij laadt alleen nog en verkoopt nooit.
2. **Hittegolf-validatie (vanaf 28-07, ~34°C Apeldoorn).** Model is gefit op data tot 30°C, dus 34°C is extrapolatie. Toetsen: voorspelde ratio bij 50% SOC ≈ 0,52 bij temp 34 / t48 30 (tegen ≈ 0,77 op een koele dag) — dus ~3400W i.p.v. ~5100W gevraagd laadvermogen. Narekenen of de gemeten ratio's daarbij in de buurt komen en of het residu vlak blijft.
3. EF-migraties (user-kant): drop `SolarSystemShutsDownDuringNegativePrices`; `FutureValueDiscountPerHour` toegevoegd, `NearTermHedgeHours`/`Fraction` verwijderd.
4. JSON `Infinity`/`NaN` crash (22-07 17:22, MVC-controller) + lege Enever gasprijs-feed.
5. Comment-opschoning was bezig toen onderbroken — meeste lange blocks al ingekort, mogelijk nog een paar razor/test-comments over.

## Diagnosed, niet-een-bug
- Setpoint requested ≠ Setpoint: Sessy-hardware klemt/tapert zelf (CC/CV, SOC-afhankelijk). API meldt geen reden. `Battery.SetpointRequested` (ons) vs `Sessy.PowerSetpoint` (device).
- 21-07 avond niet-ontladen: economisch correct (export tegen €0,30 was netto verlies vs. latere terugkoop); de niet-uitvoering die avond was de `j=1`-bug op v1.0.4.

**Te verifiëren na de eerstvolgende productie-run van v1.0.20:** vult `PlannedUnthrottledPowerW` zich, blijft de taper-fit stabiel (log: "Charge taper fitted on N samples"), en wordt het laadvenster nu vroeger gestart bij een lange goedkope periode.
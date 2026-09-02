# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.
It is a **living reference of the current state** — per-version history and the reasoning/measurements
behind changes live in `CHANGELOG.md`, `PLANNER.md` and the git log.

## What this is

SessyWeb is a home energy management system (HEMS) for households with [Sessy](https://www.sessy.nl) home batteries. It runs as a Docker container on a NAS and plans battery charge/discharge against day-ahead EPEX prices, solar forecast and consumption forecast. The horizon runs to **23:45 tomorrow** — `EPEXPricesService` fetches no further (`end = now.AddDays(1).Date.AddHours(23).AddMinutes(45)`), so it is 24-48 hours depending on the time of day. Everything is local — SQLite, no cloud beyond the ENTSO-E and WeerLive API calls.

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

- Config is loaded from `$CONFIG_PATH/appsettings.json` (+ optional `secrets.json`), **not** the project's own appsettings — the built-in `appsettings.json` is removed from the sources and not published. `CONFIG_PATH` defaults to the working directory. Sections bind to `SessyBatteryConfig`, `SessyP1Config`, `PowerSystemsConfig`, `SettingsConfig`, `WeatherExpectancyConfig`, `SolarEdgeCloudConfig`, `HeatPumpConfig`.
- **`SettingsService` must remain the first `AddHostedService`.** All other background services `await _settingsService.WaitForReadyAsync()` before their first cycle.
- Most services are singletons registered *both* as themselves and as their interface via a `sp => sp.GetRequiredService<T>()` factory — never add a second `AddSingleton<IFoo, Foo>()` alongside, that creates a duplicate instance.
- `ServiceLocator.ServiceProvider` is set post-build for the few places that resolve outside DI.

### Settings: two separate config systems

1. **`appsettings.json`** — infrastructure only: connection string, backup dir, battery/meter/inverter IPs. (`SettingsConfig` holds only `DatabaseBackupDirectory`; timezone is a DB row.) Read via `IOptionsMonitor<T>`; changes fire `OnChange`.
2. **The `Settings` DB row** — all operational tuning (strategy, cycle cost, reserve %, efficiency, consumption profile, manual override). Owned by `SettingsService`; read via `_settingsService.Current`; after the UI saves, call `SettingsService.RefreshAsync()` which fires `SettingsChanged(Settings, bool isStartup)`.

Services cache `Settings` in a field and refresh it in the `SettingsChanged` handler. When adding a setting that must trigger a plan rebuild, set a rebuild reason in that handler (see `MilpServiceBase._configChangedReason`).

Config that must react to a live change is read via `IOptionsMonitor` + `OnChange`, never `IOptions` (which freezes at first resolution). Scoped subscribers in Blazor Server must unsubscribe via `IDisposable` or a closed circuit keeps a dead object alive. Bootstrap-only values (timezone, connection string, backup dir) are deliberately *not* monitored — they only matter at restart.

### Planning pipeline

`BatteriesService` (the main loop, a `BackgroundHeartbeatService`, 60s / 10s in DEBUG) drives each cycle:

1. Gather `List<QuarterlyInfo>` — one object per quarter-hour holding price, solar, consumption, mode and SOC. `QuarterlyInfo` is the central data structure passed between the planner, the UI and the DB writers; measured quarters are constructed from a `QuarterlyMeasurement` (`IsMeasured = true`), future quarters via `CreateAsync`.
2. `IMilpService.BuildPlanAsync(quarterlyInfos, currentSocWh)` — rebuilds only when needed (price change, SOC deviation > 20%, settings change).
3. `EnergySystemStateMachine.Evaluate(EnergySystemInput)` decides the actual mode.
4. Execute one action per quarter via the Sessy Open API (`SessyService` / `BatteryContainer` / `Battery`).

**Strategy selection**: `IMilpService` is registered as `MilpServiceProxy`, which dispatches per call to `ProfitMaximizationMilpService`, `SelfConsumptionMilpService`, `BalancedMilpService` or `BatterySavingMilpService` based on `Settings.Strategy` — so a strategy change in the UI takes effect without a restart. All four derive from `MilpServiceBase` (input gathering, SOC bookkeeping, plan persistence — the search itself is `BatteryGreedyPlanner`); the per-strategy deltas live in `Services/Optimization/Strategies/`. Put shared planner changes in `MilpServiceBase`, objective/constraint differences in the strategy.

### State machine

`EnergySystemStateMachine` is the single place where battery mode and inverter setpoint are decided — "all transition logic lives here, nowhere else". Curtailment (negative selling price) overrides the plan. Two separate enums, do not conflate them: the battery mode is `Modes` (`Unknown`, `Charging`, `Discharging`, `ZeroNetHome`, `Disabled`), the curtailment action is `CurtailmentMode` (`None`, `ZeroExport`, `Throttle`, `Shutdown`). `InverterCurtailmentService` polls `CurrentAction` every 5s. It has no DI dependencies beyond a logger, which is why it is the most heavily unit-tested class — extend `EnergySystemStateMachineTests`' input matrix when adding a transition.

Anti-flapping lives here too (see "Belangrijke mechanismen"): a `MinimumModeDwell` backstop plus deadband/hysteresis in the guards.

### Solar sources

Every source implements `ISolarInverterService` and is activated by its `ProviderName` appearing as a key in `PowerSystems:Endpoints`; `SolarInverterManager` holds the active ones and is the only thing the rest of the system talks to. Two kinds exist: `SunspecInverterService` (Modbus TCP, one thin subclass per brand) and `SessyInverterService` (the batteries' own CT clamps). They are mutually exclusive — the same panels — and `FillActiveInverterServices` drops the others when `Sessy` is present.

Three rules for anything that touches this area:

- **0 W is a measurement only when it is dark.** A source that cannot read must report `IsAvailable = false`, never 0 W: consumption is `solar + grid + battery`, so a silent zero is stored as a household that used the whole solar production less than it did. `SolarIsMeasurable` is the gate.
- **Curtailment is a capability, not a reachability.** `SupportsCurtailment` → `CurtailmentIsPossible` → `EnergySystemInput` → `EvaluateCurtailment`, which falls back to the plan when nothing can be throttled. FORCE_CHARGE draws maximum grid power *because* it assumes the inverter went to 0 W — that assumption is what the flag protects.
- **Write `InverterMeasurements`.** SolarPowerPage, the statistics and `SolarService.CalculateHistoricalPerformanceFactorAsync` read that table, not the live value. Use `InverterMeasurementWriter`.

### Data access

`DataService` classes derive from `ServiceBase<T>` (`Add`, `AddOrUpdate`, `Update`, `Remove`, `Get`, `GetList`, `Query`, `RemoveWhere`) and go through `DbHelper`, which creates a fresh scope + `ModelContext` per call.

- **Writes** run through one **static** `SemaphoreSlim` — process-wide, because SQLite allows only one writer.
- **Reads** run through `ExecuteQueryAsync` (max 4 concurrent per data-service) and no longer block a thread. Never bring back the old synchronous `ExecuteQuery` with `Wait()` — it parked a thread-pool thread per query and caused seconds-long GUI stalls.
- Never hold a `ModelContext` across calls; never call a data-service *inside* another write's callback (deadlock on the static write lock).
- Reads use `AsNoTracking()`, so returned entities are detached — write back through `AddOrUpdate`/`Update`.
- `AddOrUpdate`/`Update`/`Remove` require `T : IUpdatable<T>` and use that `Update()` implementation to copy fields.
- `ExecuteTransaction` throws if `SaveChangesAsync` writes 0 rows. `RemoveWhere` (bulk delete) therefore runs via `ExecuteWriteAsync` **without** a transaction — `ExecuteDelete` bypasses the change-tracker and would otherwise be rolled back.
- Large upserts: pass `ServiceBase.MatchOn(key, window)` to `AddOrUpdate` instead of a `contains`-lambda that queries per row.
- Connections get `synchronous=NORMAL` and `busy_timeout=5000` via `SqlitePragmaInterceptor`; `journal_mode=WAL` is **not** set there but once at startup via `SqliteSetup.EnableWriteAheadLogging` (it is a write action).

### Background services

Long-running services derive from `BackgroundHeartbeatService` (a `BackgroundService` with an `OnHeartBeat` event so the UI can trigger an immediate cycle). Since they are singletons and DB services are scoped-per-call, they create one `IServiceScope` in the constructor and resolve from it — follow that pattern rather than injecting scoped services directly.

### Time

Never use `DateTime.Now`. Use `TimeZoneService.Now` (configured timezone, `virtual` so tests can mock it) and the `DateTimeExtension` helpers (`DateFloorQuarter()` etc.). Quarter-hour alignment is assumed everywhere in the planner.

### UI

Blazor Server, Radzen components, `.razor` + `.razor.cs` code-behind pairs. Pages inherit `PageBase`, components inherit `BaseComponent` — both inject `BatteryContainer`/`BatteriesService` and expose `IsManualOverride`, `WeAreInControl`, `ChargedInControl`, a cascading `ScreenInfo` (mobile/landscape detection via BlazorSize) and `GetFormatProvider()` (hardcoded `nl-NL`). `PageBase` additionally cascades `SetIsBusy` for the global spinner and sets `HideId` from the DEBUG flag. Swagger is exposed at `/swagger`. Verify Radzen APIs against `raw.githubusercontent.com/radzenhq/radzen-blazor/master/...` before use.

## Deployment

`SessyWeb/Dockerfile` builds and publishes SessyWeb. Container expects volumes at `/SessyController/Config` (appsettings.json) and `/SessyController/Data` (SQLite DB + backups), ports 80/443, and `CONFIG_PATH=/SessyController/Config`. See README.md for the full Synology Container Manager setup.

---

# Samenvatting (NL)

## Wat is SessyWeb
C#/.NET Blazor Server EMS. Stuurt 3× Sessy batterij (cap 16,2 kWh; raw charge 6600W/discharge 5100W;
**aantoonbaar gehaald ~5,0-5,3 kW laden** over vrijwel het hele SOC-bereik — de eerdere "praktijk max
~4,4kW" was de getaperde planwaarde, niet de hardwarelimiet), SolarEdge inverter, Daikin warmtepomp.
Draait op Synology NAS via Docker. Huidige versie **v1.0.115**. Locatie: Apeldoorn. De zonmeting staat
op de **Sessy-bron** in plaats van SolarEdge-Modbus; de omvormer hangt er nog maar wordt niet meer
uitgelezen (bewust, om die bron te ijken — zie openstaand punt 9).

Er draaien ook instanties bij anderen — meldingen komen als GitHub-issues binnen op
`PaulSinnema/SessyWeb`. Die configuraties wijken af (één batterij, geen zon, geen warmtepomp).

## Werkafspraken
- Antwoorden in het Nederlands, caveman-ultra kort. Code-commentaar in het Engels, **kort — één regel waar mogelijk** (user leest alle comments na ter controle).
- **Niet gissen**: eerst verifiëren in code/DB/web. Meerdere sessies gingen mis door aannames — meerdere keren "gevonden!" geroepen en ernaast gezeten. Bij planner-analyse: DB-tabellen naast elkaar leggen en de opgenomen solve-invoer door de échte planner terugspelen vóór conclusies.
- Code-behind boven `@code`-blokken. Radzen Blazor overal. Radzen API's verifiëren via `raw.githubusercontent.com/radzenhq/radzen-blazor/master/...` (rendering-pad én crosshair/tooltip-pad — `CartesianSeries.DataAt/TooltipY` unwrapt `double?` hard).
- Na codegeneratie altijd zelf reviewen op syntax/naamfouten/dubbele code/missing usings. Braces + code-parens balanceren (comments negeren bij paren-telling).
- **Versie ophogen bij ELKE wijziging, plus een CHANGELOG-regel** — één afspraak, hierboven onder "Repository conventions". Niet hier herhalen; twee kopieën lopen uit elkaar.

## Omgeving
- Broncode én werkkopie: `C:\Projects\Sessy` (Windows, PowerShell, dotnet aanwezig — bouwen en testen kan direct).
- Productie-DB staat lokaal: `SessyWeb/SessyController/Data/Sessy.db` (+ WAL/shm). Read-only openen voor analyse.
- Planner-onderzoek gaat via de **échte** planner, niet via een spiegel: `SolveInputRecorder`
  (`SESSY_RECORD_SOLVE_INPUTS`) neemt de solve-invoer op, en de opgenomen JSON speelt terug door
  `BatteryGreedyPlanner` zelf (zie `RealDayPlannerTests` / `EveningDischargeProbeTests`). Een oude
  Python-spiegel is verwijderd — die liet door een afwijkende lusgrens (`j=1` vs `range(0,n)`) een
  bug ten onrechte als "opgelost" melden.

## Planner-architectuur (BatteryGreedyPlanner.cs)
Deterministisch, greedy, twee passes: (1) ZeroNetHome-baseline, (2) arbitrage in blokken van `BlockKWh` — 0,20 kWh (puur een snelheidsknop; plannen zijn bit-identiek van 0,05 t/m 1,00 kWh). Candidate A = ontladen van al-aanwezige energie (geen gekoppeld laadmoment); Candidate B = laden-i → ontladen-j met `i<j`; Candidate C = carry-forward voorbij de horizon; Candidate D = nu verkopen, later terugkopen (ontladen j → terugkopen k>j).
- **FutureValueDiscountPerHour** (default 0,003 — UI toont procenten, dus 0,3): continue korting op waarde[j]/kosten[i]. Rapportage/objective gebruiken echte prijzen. Hoort samen met `PredictedPriceMode`: bij `Off` is >24 u toch `reserveOnly`.
- **ReplacementCostEurPerKWh**: bodemprijs (reservatieprijs) voor Candidate A — voorkomt dumpen van doorschuif-energie. Geleverd door `ReplacementCostService` (FIFO alleen nog terugval). Zon-voorraad = 0.
- `Solve` heeft een optionele `trace`-callback (null = geen extra berekening) die per kwartier meldt waaróm er niet verkocht is (waarde tegen bodem, slack, headroom).

## Zelf-gemeten modellen
Vijf modellen worden uit de productiehistorie gefit; alle vallen bij te weinig samples netjes terug.

- **ChargeTaper** (`ThrottleAnalysisService.GetChargeTaperAsync`): `ratio = A − B·soc − C·(temp−20) − D·(t48−temp)`, gewogen OLS met exponentiële veroudering (halfwaarde 120 d, cap 730 d). A/B (batterij-taper) recent (31 d) met C/D vastgezet; C/D (huisgedrag) over lang venster. Fallback: 3 regressors → SOC-only → `None`. Bij geldige taper wordt de temperatuur-charge-ratio op 1,0 gezet (anders dubbel derated).
- **ChargeCapabilityFloor** (`ThrottleAnalysisService`): per 5%-SOC-bin het P90 van gemeten laadvermogens × 0,9, geklemd op nameplate, uit elk laadkwartier (geen noemer nodig). `taperedChargeKWh = max(taper, floor)`. Bewust een bodem in watt en géén fit: een hoge meting bewíjst wat de bank kan, een lage bewijst niets.
- **DischargeCapability** (`GetDischargeCapabilityAsync`): plateau + knik in absolute watts, **géén** temperatuurtermen (nagemeten: vermogen insignificant). Plateau = mediaan bin-maxima boven half vol; knik = laagste bin waar die bin én de volgende het plateau halen. De ontlaadkant plant op SOC.
- **EfficiencyCurve** (`BatteryEfficiencyService.GetEfficiencyCurveAsync`): `rendement = plafond − overhead/vermogen`, per richting gefit. Bewuste asymmetrie: **beslissingen** lezen de curve op het vermogen dat een kwartier aankan, **boekhouding** op het vermogen dat het kwartier werkelijk kreeg. Te weinig samples → `Flat`.
- **NightReserve / ComputeMinSocWh**: reserve tot de volgende zonsopgang; bij horizon-afkapping (nacht valt voorbij 23:45 morgen) `max(zichtbare som, geleerde nachtbehoefte − zon ervoor)`. `NightReserveCapPct` geleerd door `PlannerLearningService` (P80 van gemeten nachten, cap ~33%). De nachtreserve telt niet over een zonvenster heen (`solarBeforeWh` afgetrokken).

`ReplacementCostEurPerKWh` (`ReplacementCostService`) = P25 van de dagelijks goedkoopste all-in
inkoopprijs over 30 d, geklemd op de mediaan-inkoopprijs — een geschatte toekomstige energie-inkoopprijs,
**geen** batterijvervanging. `CycleCostEurPerKWh` = echte slijtage = investering / (capaciteit × 8000 cycli),
terugval 0,05.

## Belangrijke mechanismen (huidige staat)
- **ControlModeService** — `ControlMode` = SessyWeb/Manual/Charged/Provider, prioriteit leverancier > Charged > manual > wij. `WeMayDriveTheBatteries` is de enige schrijf-conditie op de hardware; **manual override = SessyWeb die een ander plan schrijft**, geen andere bestuurder. Lezers zijn ongeguard (`GetScheduleAsync`, identieke GET voor beide richtingen). Weigeren logt op Warning.
- **Anti-flapping** (state machine) — `MinimumModeDwell` (120 s) in `EnergySystemStateMachine`, klok uit `EnergySystemInput.Now` (`DateTime.MinValue` = geen klok in de snapshot). Asymmetrisch: wissel naar een minder actieve modus mag altijd direct; her-inschakelen/wisselen tussen even actieve modi zit de dwell uit. Guards: `GuardHolds(...)` met `GuardReleaseFactor = 4`; idle: `SelectIdleMode` met `NetLoadDeadbandWh = 50`. Deadband schaalt mee: `MinimumUsefulEnergyWh(capWh) = max(25, capWh × 0,005)`.
- **QuarterlyFactsService** — de enige plek waar een gemeten kwartier wordt samengesteld, één gezaghebbende tabel per grootheid: net ← `EnergyHistory` (delta, geklemd, per `MeterId`), zon ← `InverterMeasurements`, huisbelasting ← `Consumption`, batterij ← `QuarterlyMeasurements`, prijzen ← `EPEXPrices` + `Taxes`. Drager = `MeasurementView`. Twee regels: een totaal mag nooit begrensd worden door de tabel van een ándere grootheid (`GetDataRangeAsync` bepaalt het venster over álle bronnen); duplicaten in `QuarterlyMeasurements` worden bij lezen ontdubbeld (`GetAsync` groepeert op `Time`, hoogste `Id` — de unieke index staat nog niet terug).
- **Statistiek-venster** — `Settings.StatisticsFromDate` klemt de startdatum; `EnergyStatistics.ClampedByStatisticsFromDate` + `EffectiveStart/End` maken zichtbaar wanneer dat bijt. Een "komt niet overeen"-melding tussen twee pagina's is drie keer op rij een venster-verschil gebleken, geen rekenfout — reken eerst uit wélk venster het getoonde getal oplevert.
- **Sessy zonbron** — provider-key `"Sessy"` in `PowerSystems:Endpoints` (geen aparte sectie, geen eigen IP: leest `BatteryContainer`). Leest `PowerStatus.RenewableEnergyPhase*` van álle batterijen (of een subset via `Endpoint.Batteries`) en telt op; `InverterMaxCapacity` klemt de som en telt overschrijdingen. `InverterMeasurementWriter` schrijft de historie. De binnenste `Endpoints:Sessy`-sleutel is een label (`InverterId`), geen batterijnummer.
- **Charged handoff** — op de overgang naar Charged één POST: `ProfitMaximization` → ROI, overige strategieën → ECO (`BatteriesService.MapsToRoi`). `SetActivePowerStrategyAsync(handover: true)` omzeilt de schrijfguard; vuurt één keer, niet elke cyclus. Zit binnen `#if !DEBUG`.
- **ChargedScheduleService** — haalt altijd het niet-uitvoerende schema op (`GetScheduleAsync`), max 1×/5 min; de grafiek tekent het als gestippelde schaduwlijn. Toggle `ShowOther` in de grafiekheader (default uit).
- **Config-hardheid** — de ingebakken `appsettings.json` wordt uit de bronnen verwijderd (`RemoveBuiltInAppSettings`) + `CopyToPublishDirectory=Never`; config komt uitsluitend uit `$CONFIG_PATH`, ontbreekt die dan faalt de app zichtbaar. `PowerSystemsConfig.Endpoints` / `SessyP1Config.Endpoints` / `SessyBatteryConfig.Batteries` zijn niet-nullable met lege dict als default. Config-/`secrets.json`-restanten **declareren geen apparaat**: `IsConfigured` (= `BaseUrl` gevuld) is de gate, overgeslagen sleutels staan bij naam in Tips & Checks.
- **SystemCapabilitiesService / HasSolar** — één definitie of er zon is (minstens één omvormer). De UI verbergt zon-afhankelijke kaarten, menu-items en zinsneden als er geen zon is. Precedent: `HeatPumpIsConfigured`.
- **Tips & Checks** — `ConfigurationCheckService` draait de checks periodiek en houdt een samenvatting vast (`EnsureSummaryAsync`, 5 min, single-flight); een notificatiestip (rood/oranje) staat op het menu-item en de tab Settings, aangestuurd via `data-sessy-badge` (Radzen overschrijft een meegegeven `class`).

## In uitvoering — nog niet in productie

**(Ont)laden via P1 grid target i.p.v. batterij-setpoint (fase 1).** Build + tests groen, nog niet
in productie gedraaid. Batterijvermogen gaat niet meer via `SetPowerSetpointAsync` maar via de grid
target op de P1-meter (`P1MeterService.SetGridTargetAsync`, `POST /api/v1/meter/grid_target`); de
batterijen draaien daarvoor in NOM, want alleen dan luistert de Sessy naar de P1-grid-target.

- Mechanisme: in NOM houdt de Sessy `net = grid_target`, dus `batterij = huisnetto − grid_target`, met `huisnetto = P1net + batterij` (per cyclus herrekend). Tekens: grid target import +, export −; `P1Details.PowerTotal` import +, export −; batterij ontladen +, laden −.
- Mode-mapping: Charging → NOM + `grid_target = huisnetto + P`; Discharging → NOM + `huisnetto − P`; ZeroNetHome → NOM + `grid_target = 0`; Disabled → API + `SetPowerSetpointAsync(0)`.
- `GridTargetCalculator` (pure omrekening + clamp op nameplate), `GridTargetService` (5 s-refresher, deadband 50 W, `weDrive`-gate). In DEBUG geen POST — `LastComputedTargetW` toont de would-be waarde op `BatteriesPage`. FORCE_CHARGE/ZERO_EXPORT lopen gedwongen mee door dit pad.
- **Werkwijze bij deze ombouw:** in de services wordt géén code verwijderd — vervangen methoden (`StartCharging`/`StartDisharging`, het setpoint-pad voor (ont)laden) blijven als dode code staan. Buiten de services mag dode code wel weg.
- **Fase 2 (te doen):** de omvormer-kant van curtailment reconciliëren; checken of `GridTargetService` en `InverterCurtailmentService` niet botsen op de P1; DI-graaf en echt gedrag in een draaiende app verifiëren (let op `GridTargetService started ...` en of de batterij op de gewenste P uitkomt).

## Valkuilen / eenheden
- `SolarPowerPerQuarterInWatts` is **Wh per kwartier** (`=> SolarPowerPerQuarterHour * 1000.0`), niet W. Met W×0,25 gerekend lijkt de zonforecast een factor 4 te laag.
- `Consumption.ConsumptionWh` is **Watt gemiddeld over het kwartier**, niet Wh. Conversie op één plek: `MeasurementView.ConsumptionKWh`.
- `QuarterlyMeasurement.BatteryMode` is óns geplande mode, niet de hardware-keuze.
- Prijseenheden: ENTSO-E EUR/MWh ÷1000, Sessy ÷100000 → EUR/kWh; ENTSO-E levert PT60M (uur) → `ExpandToQuarters` (een prijs is een tarief, niet delen).
- `consumption = solar + net + battery` — een ontbrekende term wordt stil als verkeerd verbruik opgeslagen; daarom de `SolarIsMeasurable`-gate en `null`-in-plaats-van-partiële-som.

## Diagnostiek
Instrumentatie die blijft loggen (prod log-level = Warning): `DbHelper` (trage wacht-/houdtijden),
`ThreadPoolMonitorService` (oplopende werkqueue), `RenderTimer` ("Slow render: <component> took N ms"),
`Clock` ("UI blocked: clock tick ran N ms late"). `BatteriesService.WatchStrategy` meldt `STRATEGY_CHURN`
(≥4 modewissels/kwartier) en `FOREIGN_STRATEGY` (hardware rapporteert 3 cycli een andere strategie dan
gecommandeerd — bewijs van een tweede schrijver). Bij "geen logs om te plakken": op Warning-niveau
selecteert dat juist de stille takken — zoek daar eerst.

## Openstaande punten
*De nummering heeft een gat (2 is vervallen). Niet hernummeren — elders wordt naar "Openstaande punten 4" verwezen.*

0. **De taper-fit zelf blijft scheef.** De gemeten bodem (`ChargeCapabilityFloor`) dekt het praktische
   probleem af, maar de fit herstelt pas met maanden `PlannedUnthrottledPowerW`-dekking over een breed
   temperatuurbereik; Tips & Checks laat zien hoe ver hij ernaast zit. De verworpen oplossing ("fit op
   watt") kwam er fysiek onmogelijk uit (positieve SOC-helling) — niets forceren tot die dekking er is.
1. **Productie-verificatie van de ontlaadkant, de NaN-fix en de gasprijs-fix staat nog open** — alleen
   lokaal getoetst.
3. **De discount is nooit eerlijk getoetst.** `FutureValueDiscountPerHour` hedget voorspelfouten,
   maar elke replay tot nu toe rekent af tegen dezelfde forecast waarmee gepland is. Een harnas dat
   afrekent tegen gemeten verbruik en zon (`QuarterlyMeasurements` / `EnergyHistory`) zou de vraag
   wél beantwoorden — en meteen die van `PredictedPriceMode` en de nachtreserve.
4. **Greedy laat geld liggen — gemeten op ~€145/jaar (band €70–200).** Een langere horizon levert
   minder op, wat alleen bij een suboptimale heuristiek kan. Een DP over (kwartier, SOC-niveau) geeft
   het optimum onder dezelfde constraints (naar schatting sneller dan de huidige zoektocht), maar is
   een herbouw van de kern. Backtest (productie-DB, 25 mei–31 aug 2026, open-loop, Charged-dagen eruit):
   DP wint ~€0,40/dag ≈ €145/jaar, ~€184/jaar bij perfecte vooruitblik; de winst schaalt met de
   dagelijkse prijsspread. Alleen zomerdata. Ter contrast: "zon exporteren i.p.v. opslaan bij hoge
   prijzen" levert maar ~€5–12/jaar — de planner is de veel grotere hefboom.
5. **`SessyWeb.Helpers.ScreenInfo` is niet getest** omdat `SessyUnitTests` geen projectreferentie
   naar `SessyWeb` heeft. Twee wegen: `ScreenInfo` naar `SessyCommon` verhuizen (pure helper, maar
   `PageBase`/`BaseComponent`/`_Imports` moeten mee), of `SessyWeb` als projectreferentie toevoegen.
6. **`ConsumptionMonitorService` is niet getest.** Hangt aan `P1MeterService`, `SolarInverterManager`
   en `BatteryContainer` — concrete klassen zonder interface, dus niet te mocken. `CalculateConsumption`
   (partiële som → `null`) en `WaitForWeatherAsync` (opstart-gratie, daarna niet blokkeren) verdienen
   een test zodra die drie een interface krijgen. Geldt ook voor `BatteriesService.WatchStrategy` en de
   Tips & Checks-indicator.
7. **De Sessy-bron is gebouwd maar bij externe melders niet bevestigd.** Vraag de provider-key `"Sessy"`
   te configureren en dan Tips & Checks; die noemt alle faalgevallen bij naam. Werkt het niet, dan is de
   vraag of de Sessy's CT-klemmen om de PV-groep hebben — zonder die bedrading meet ook de Sessy niets
   (de P1-respons `P1Details` heeft géén PV-veld).
8. **`SolarInverterManager.ExecuteAsync` bereikt zijn health-check nooit met een Modbus-bron.**
   `SunspecInverterService.Start` draait zijn lus *inline*, dus `RunHealthCheckLoopAsync` komt na de
   `foreach` nooit aan de beurt. Gevolg: `CheckAvailabilityAsync` zet `IsAvailable` nooit, die blijft
   eeuwig `true`, en de `SolarIsMeasurable`-guard is voor Modbus in de praktijk dood.
   `SessyInverterService.Start` keert wél meteen terug (`Task.Run`), dus met de Sessy-bron loopt de
   health-check — en die vuurde meteen vals (de nacht als storing). Fix is één regel, maar zet een keten
   aan die nog nooit gedraaid heeft — eerst meten wat `LastSuccessfulReadUtc` in productie doet.
9. **De Sessy-zonmeting is nooit tegen een referentie gelegd.** `renewable_energy_phase*` wordt nergens
   bewaard, dus er valt niets retroactief te toetsen. Twee open vragen: (a) meet alleen batterij 1 (uit
   een 21:38-meting; `Batteries: ["1"]` staat nu in de config) en (b) hoe verhoudt de Sessy-som zich tot
   de echte productie. De simultane vergelijking kan niet meer via de config (óf-óf-regel sluit SolarEdge
   uit); wél: één zonnige dag terug op SolarEdge en de batterijen er los naast bemonsteren met
   `GET /api/v1/power/status` (zelfde kwartieren, beide bronnen).
10. **De view toont niet 1-op-1 het berekende plan — lossy conversie + reconstructie in de view.**
    Het plan (`PlanStep`: aparte `ChargeKW`/`DischargeKW` + `ActionMode`) wordt onderweg twee keer
    platgeslagen: `ApplySolveResult` → `PlanAction { Mode, PowerW }` (bij ZeroNetHome is `PowerW`
    alléén het ontlaadvermogen; een ZeroNetHome die zon opslaat verliest de laadwaarde hier al), en
    `SavePlan` → DB-`PlannedQuarter { PlannedMode (string), PlannedPowerW (één signed getal) }`.
    Vervolgens **reconstrueert** `QuarterlyInfoView` (~r90) laden/ontladen uit de mode-string met een
    switch die alléén "Charging"/"Discharging" kent — dus elke ZeroNetHome/Disabled valt op 0, en
    zelfconsumptie-ontlading (mode ZeroNetHome, kan meerdere kWh zijn) wordt als 0 getoond. De
    live-tak (`plannedQuarter == null`) leest wél `qi.PlannedDischargePowerW` correct → zelfde
    kwartier, twee uitkomsten afhankelijk van de bron. Bevestigd via replay (02-09): greedy plant
    5,56 kWh ontlading (46 kw ZeroNetHome-zelfconsumptie, 0 actief export, 1 laadkwartier van
    1,11 kWh = de "1 kwartier geladen") — de planner is dus niet stuk, de view verliest het onderweg.
    Richting single-source-of-truth: `PlannedQuarter` twee aparte vermogensvelden geven zoals de
    planner ze levert, de view die rechtstreeks lezen, de mode-string alleen voor label/kleur, en één
    projectie `PlanStep → (chargeW, dischargeW, mode)` die zowel de live- als de DB-tak voedt.
    v1.0.115 trok alleen de overlay (`ChargingHoursChartComponent.PlanPowerVisualFor`) gelijk; de
    hoofdoorzaak (reconstructie uit de mode-string in `QuarterlyInfoView`) staat nog open.

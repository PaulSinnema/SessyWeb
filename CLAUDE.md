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
- **Versioning follows "bump version"**: the version lives only in `SessyCommon/AppInfo.cs` (`public const string Version = "v1.0.x";`); MainLayout shows it and `Program.cs` writes it into the `AppVersions` table at startup. Bump **only when the user says "bump version"**: that means the current version is committed and pushed, so open the next one — patch +1 in `AppInfo.cs` and a new `CHANGELOG.md` heading with "No functional changes.".
- **Changes go under the open version**: every change after a bump is added to the `CHANGELOG.md` entry of that open version (replacing "No functional changes."), newest version at the top, grouped as Added/Changed/Fixed/Removed. Keep it to what a user notices, in English, a few lines per version — the reasoning, the measurements and the rejected alternatives belong in this file instead. A change with nothing observable (a refactor, a test) needs no changelog entry.
- **Keep `SETTINGS.md` in sync** (upper case — README links it and GitHub is case-sensitive): when a setting on the Settings page is added, removed or changed (label, default or effect), update `SETTINGS.md` in the same change — it is meant to be a complete mirror of the page.
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
2. `IMilpService.BuildPlanAsync(quarterlyInfos, currentSocWh)` — rebuilds only when needed (price change, SOC deviation > 20%, settings change, measured SOC at the planner's reserve floor while the plan says ZeroNetHome), plus a speculative solve every quarter.
3. `EnergySystemStateMachine.Evaluate(EnergySystemInput)` decides the actual mode.
4. Execute one action per quarter via the Sessy Open API (`SessyService` / `BatteryContainer` / `Battery`).

**Strategy selection**: `IMilpService` is registered as `MilpServiceProxy`, which dispatches per call to `ProfitMaximizationMilpService`, `SelfConsumptionMilpService`, `BalancedMilpService` or `BatterySavingMilpService` based on `Settings.Strategy` — so a strategy change in the UI takes effect without a restart. All four derive from `MilpServiceBase` (input gathering, SOC bookkeeping, plan persistence — the search itself is `BatteryGreedyPlanner`); the per-strategy deltas live in `Services/Optimization/Strategies/`. Put shared planner changes in `MilpServiceBase`, objective/constraint differences in the strategy.

### State machine

`EnergySystemStateMachine` is the single place where battery mode and inverter setpoint are decided — "all transition logic lives here, nowhere else". Curtailment (negative selling price) overrides the plan. Two separate enums, do not conflate them: the battery mode is `Modes` (`Unknown`, `Charging`, `Discharging`, `ZeroNetHome`, `Disabled`, `HoldReserve`), the curtailment action is `CurtailmentMode` (`None`, `ZeroExport`, `Throttle`, `Shutdown`). `InverterCurtailmentService` polls `CurrentAction` every 5s. It has no DI dependencies beyond a logger, which is why it is the most heavily unit-tested class — extend `EnergySystemStateMachineTests`' input matrix when adding a transition.

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

**Colours**: one palette in `wwwroot/css/site.css` (`--sessy-*` tokens). Never hard-code a colour in a page or chart: use `var(--sessy-x)` for fills (`-area`/`-faded` for translucent), `var(--sessy-x-line)` for chart strokes and `var(--sessy-x-text)` for coloured text — the `-line`/`-text` variants mix in `--rz-text-color`, so they keep contrast on light and dark themes. C#-built markup uses `Helpers/SessyColors` (mode badges, mode text). Bright badges/buttons (warning/info/success) get dark text globally from site.css — don't add per-page `color:` overrides. Give every chart series an explicit colour (Radzen's default palette runs out and then draws nothing).

## Deployment

`SessyWeb/Dockerfile` builds and publishes SessyWeb. Container expects volumes at `/SessyController/Config` (appsettings.json) and `/SessyController/Data` (SQLite DB + backups), ports 80/443, and `CONFIG_PATH=/SessyController/Config`. See README.md for the full Synology Container Manager setup.

---

# Samenvatting (NL)

## Wat is SessyWeb
C#/.NET Blazor Server EMS. Stuurt 3× Sessy batterij (cap 16,2 kWh; raw charge 6600W/discharge 5100W;
**aantoonbaar gehaald ~5,0-5,3 kW laden** over vrijwel het hele SOC-bereik — de eerdere "praktijk max
~4,4kW" was de getaperde planwaarde, niet de hardwarelimiet), SolarEdge inverter, Daikin warmtepomp.
Draait op Synology NAS via Docker. Huidige versie: zie `SessyCommon/AppInfo.cs`. Locatie: Apeldoorn. De zonmeting staat
op de **Sessy-bron** in plaats van SolarEdge-Modbus; de omvormer hangt er nog maar wordt niet meer
uitgelezen (bewust, om die bron te ijken — zie openstaand punt 9).

Er draaien ook instanties bij anderen — meldingen komen als GitHub-issues binnen op
`PaulSinnema/SessyWeb`. Die configuraties wijken af (één batterij, geen zon, geen warmtepomp).

## Werkafspraken
- Antwoorden in het Nederlands, caveman-ultra kort. Code-commentaar in het Engels, **kort — één regel waar mogelijk** (user leest alle comments na ter controle).
- **Niet gissen**: eerst verifiëren in code/DB/web. Meerdere sessies gingen mis door aannames — meerdere keren "gevonden!" geroepen en ernaast gezeten. Bij planner-analyse: DB-tabellen naast elkaar leggen en de opgenomen solve-invoer door de échte planner terugspelen vóór conclusies.
- Code-behind boven `@code`-blokken. Radzen Blazor overal. Radzen API's verifiëren via `raw.githubusercontent.com/radzenhq/radzen-blazor/master/...` (rendering-pad én crosshair/tooltip-pad — `CartesianSeries.DataAt/TooltipY` unwrapt `double?` hard).
- Na codegeneratie altijd zelf reviewen op syntax/naamfouten/dubbele code/missing usings. Braces + code-parens balanceren (comments negeren bij paren-telling).
- **Versie alleen ophogen op "bump version", wijzigingen in de CHANGELOG van de open versie** — één afspraak, hierboven onder "Repository conventions". Niet hier herhalen; twee kopieën lopen uit elkaar.
- **Bestandsnamen: altijd het origineel, exact zoals het op schijf staat (incl. hoofd/kleine letters).** Nooit de schrijfwijze uit docs of verwijzingen overnemen — eerst de map bekijken. Windows is hoofdletter-ongevoelig, Git niet: een bestand wegschrijven als `Settings.md` hernoemde `SETTINGS.md` stil, waarna de Git-index beide namen had en er altijd één als 'D' stond (v1.0.152; alleen op te lossen door verwijderen, beide deletes committen en opnieuw toevoegen). Een bewuste hoofdletterwijziging alleen via `git mv`.

## Omgeving
- Broncode én werkkopie: `C:\Projects\Sessy` (Windows, PowerShell, dotnet aanwezig — bouwen en testen kan direct).
- Productie-DB staat lokaal: `SessyWeb/SessyController/Data/Sessy.db` (+ WAL/shm). Read-only openen voor analyse.
- Planner-onderzoek gaat via de **échte** planner, niet via een spiegel: `SolveInputRecorder`
  (`SESSY_RECORD_SOLVE_INPUTS`) neemt de solve-invoer op, en de opgenomen JSON speelt terug door
  `BatteryGreedyPlanner` zelf (zie `RealDayPlannerTests` / `EveningDischargeProbeTests`). Een oude
  Python-spiegel is verwijderd — die liet door een afwijkende lusgrens (`j=1` vs `range(0,n)`) een
  bug ten onrechte als "opgelost" melden.

## Planner-architectuur (BatteryGreedyPlanner.cs)
Deterministisch, greedy: (1) ZeroNetHome-baseline, (2) arbitrage in blokken van `BlockKWh` — 0,20 kWh (puur een snelheidsknop; plannen zijn bit-identiek van 0,05 t/m 1,00 kWh), (3) `RecoverHouseCover`, (4) `BuildPlanAndClassify`. Candidate A = ontladen van al-aanwezige energie (geen gekoppeld laadmoment); Candidate B = laden-i → ontladen-j met `i<j` (op het pad-SOC-cap, `MinPairBlockKWh` 0,01); Candidate C = carry-forward voorbij de horizon; Candidate D = nu verkopen, later terugkopen (ontladen j → terugkopen k>j); Candidate E = laden-i, doorschuiven naar j en leeglopen over de knik-begrensde staart (`FillTail`, alleen met gemeten `DischargeCapability`); Candidate F = ontlading verschuiven tussen kwartieren (huisdekking later inleveren voor een duurdere verkoop eerder, of andersom), exacte `Drain`/`InverseDrain`, marge 0,01 €/kWh, alleen met `Settings.ShiftDischargeEnabled` (`SessyOptions.AllowShift`). Een kwartier dat F volledig leegmaakt heet `Held`.
- **Plan = de waarheid (v1.0.149–151).** De uitvoering volgt het plan letterlijk; er wordt niets meer runtime bijgestuurd behalve de guards. Daarom moet elk label exact uitvoerbaar zijn: **ZeroNetHome dekt runtime het héle huis** (NOM, target 0), dus ZNH mag alléén waar het plan het huis volledig dekt (tot `CappedDischargeKWh`). Deeldekking of geen dekking → **HoldReserve** (opslaan bij overschot > 0, nooit ontladen bij overschot <= 0 — let op `> 0` / `<= 0`, nul hoort bij "geen overschot"). Op/onder de reserve (`atReserve` of `coverCrossesReserve`) → HoldReserve, tenzij laden uit het net of export.
- **`RecoverHouseCover`** (na arbitrage): (1) deeldekking weggooien (de opslag blijft staan), (2) SOC-pad zoals de classificatie het rekent, (3) huisdekking terugzetten, duurste kwartieren eerst, alles-of-niets per kwartier, alleen binnen de slack boven `ReserveFloor` over de rest van de horizon en zonder een latere ontlading boven zijn knik-cap te duwen. Reden: de baseline dekt het huis op het pad vóór arbitrage; arbitrage laadt daarna bij en het pad stijgt, maar de import bleef staan (replay 06-10: 1,16 kWh ongepland, onder de reserve geëindigd).
- **`ReserveFloor`** (v1.0.150, vervangt `MinSocFrom` voor huisdekking/classificatie/recover): achterwaarts `F[t] = max(MinSoc[t], F[t+1] − zon die de baseline in t+1 opslaat)`. Een verbruikskwartier geeft de vloer ongewijzigd door, een zonkwartier verlaagt hem. `MinSocFrom` (suffix-max) hield bij berekende reserve vannacht al de volle nachtreserve van morgenavond vast. Arbitrage gebruikt nog steeds per-kwartier `MinSoc` met pad-slack. De vloer gaat per kwartier mee in `PlanStep.ReserveFloorKWh` en is wat de runtime-trigger gebruikt.
- **FutureValueDiscountPerHour** (default 0,003 — UI toont procenten, dus 0,3): continue korting op waarde[j]/kosten[i]. Rapportage/objective gebruiken echte prijzen. Hoort samen met `PredictedPriceMode`: bij `Off` is >24 u toch `reserveOnly`.
- **ReservationPriceEurPerKWh** (SessyOptions; hernoemd v1.0.124, heette `ReplacementCostEurPerKWh` — was nooit batterijvervanging): bodemprijs (reservatieprijs) voor Candidate A — voorkomt dumpen van doorschuif-energie. Geleverd door `ReplacementCostService` (FIFO alleen nog terugval). Zon-voorraad = 0.
- `Solve` heeft een optionele `trace`-callback (null = geen extra berekening) die per kwartier meldt waaróm er niet verkocht is (waarde tegen bodem, slack, headroom).

## Zelf-gemeten modellen
Vijf modellen worden uit de productiehistorie gefit; alle vallen bij te weinig samples netjes terug.

- **ChargeTaper** (`ThrottleAnalysisService.GetChargeTaperAsync`): `ratio = A − B·soc − C·(temp−20) − D·(t48−temp)`, gewogen OLS met exponentiële veroudering (halfwaarde 120 d, cap 730 d). A/B (batterij-taper) recent (31 d) met C/D vastgezet; C/D (huisgedrag) over lang venster. Fallback: 3 regressors → SOC-only → `None`. Bij geldige taper wordt de temperatuur-charge-ratio op 1,0 gezet (anders dubbel derated).
- **ChargeCapabilityFloor** (`ThrottleAnalysisService`): per 5%-SOC-bin het P90 van gemeten laadvermogens × 0,9, geklemd op nameplate, uit elk laadkwartier (geen noemer nodig). `taperedChargeKWh = max(taper, floor)`. Bewust een bodem in watt en géén fit: een hoge meting bewíjst wat de bank kan, een lage bewijst niets.
- **DischargeCapability** (`GetDischargeCapabilityAsync`): plateau + knik in absolute watts, **géén** temperatuurtermen (nagemeten: vermogen insignificant). Plateau = mediaan bin-maxima boven half vol; knik = laagste bin waar die bin én de volgende het plateau halen. De ontlaadkant plant op SOC.
- **ChargeCapability** (v1.0.146, `ThrottleAnalysisService.GetChargeCapabilityAsync`): mediaan SOC-winst (DC-W) per 10%-bin over 60 d, alleen kwartieren met volle vraag (≥ 0,9 × nameplate `PlannedUnthrottledPowerW`) en vorig kwartier ook laden, ≥ 8 samples. Wint van taper/floor waar de bin data heeft; DC→AC via `ChEffFor`.
- **DischargeCapability.WithSustainedPlateau** (v1.0.146): mediaan van aanhoudende vol-gevraagde ontlaadkwartieren boven de knik (ratio 0,85, bins ≥ 5 samples, ≥ 3 bins); verlaagt het plateau alleen.
- **EfficiencyCurve** (`BatteryEfficiencyService.GetEfficiencyCurveAsync`): `rendement = plafond − overhead/vermogen`, per richting gefit. Bewuste asymmetrie: **beslissingen** lezen de curve op het vermogen dat een kwartier aankan, **boekhouding** op het vermogen dat het kwartier werkelijk kreeg. Te weinig samples → `Flat`.
- **Minimum reserve (v1.0.160, vervangt de nachtreserve)**: `BuildContextAsync` zet per kwartier `MinSocWh` = `MinimumReserveRatio` × capaciteit (`Settings.FixedNightReservePct`, label *Minimum reserve (%)*, default 0), zonder safety-factor en zonder bridge-reserve. Reden: de planner weegt nachtdekking zelf tegen avondverkoop, carry-forward waardeert wat aan het eind van de horizon over is, voorspelde kwartieren zitten in de horizon, en het BMS beschermt de cellen. Migratie `MinimumReserveReplacesNightReserve`: wie de berekende reserve gebruikte krijgt 0, een handmatige vaste reserve blijft.
  Dode code (bewust laten staan): `ApplyCalculatedNightReserve` (nooit aangeroepen), `ComputeMinSocWh` (in productie alleen nog door die methode), `NightCapRatio` (alleen door die methode), `PlannerLearningService.FitNightReserve` (`ApplyAsync` krijgt `null`, dus `NightReserveCapPct` wordt niet meer geschreven). De settings `UseCalculatedNightReserve`, `NightReserveCapPct` en `ReserveSafetyFactor` bestaan nog in het model maar worden alleen door die dode code gelezen. Oud gedrag (v1.0.122–159): reserve tot de volgende zonsopgang, geleerde P80-nachtbehoefte bij horizon-afkapping, of vaste reserve × safety-factor.

`ReservationPriceEurPerKWh` (geleverd door `ReplacementCostService`) = P25 van de dagelijks goedkoopste all-in
inkoopprijs over 30 d, geklemd op de mediaan-inkoopprijs — een geschatte toekomstige energie-inkoopprijs,
**geen** batterijvervanging. `CycleCostEurPerKWh` = echte slijtage = investering / (capaciteit × 8000 cycli),
terugval 0,05. **Sinds v1.0.125** kiest `SettingsService.CycleCost` de bron via `Settings.UseCalculatedCycleCost`:
aan = afgeleid uit investeringen (huidig gedrag), uit = vaste `FixedCycleCostEurPerKWh` (default 0 = wear-kost uit,
planner handelt op elke rendabele spread). De override zit in `ApplyDerivedCycleCostAsync`, zodat álle lezers
(planner, runtime-guards, stats, checks) dezelfde waarde krijgen. Instelbaar op de Settings-pagina onder Advanced planning parameters.

**Zon-forecast — performance-factor (bevinding v1.0.144).** Het basismodel (`SolarService.CalculateSolarPowerPerQuarterHour`)
rekent met GHI × `GetSolarFactor` = `max(0, cos θ_incidentie)` — **alleen directe straling, geen diffuus**. Een globale
schaalfactor `_smoothedPerformanceFactor` = realized/base over 14-30 d corrigeert dat (geklemd op [0,2, 3,0], per dag gereset
naar de historische waarde; `BatteriesService` is singleton en captured de Scoped `SolarService`, dus die state persisteert).
Gemeten op productie: factor ≈ **1,76, niet geklemd** (Realized 120,9 / Forecast 68,8 kWh over 14 d) — het 14-daags totaal
klopt dus al. De fout is conditie-afhankelijk (vórm, niet magnitude): benodigde factor ≈ **4,1 bij bewolking (kt<0,3),
1,5 heiig, 1,3 helder** — één scalar is te hoog op heldere en te laag op bewolkte dagen (de ~1,5-2× die gebruikers soms zien).
Onderzocht en **bewust verworpen**: (a) clamp verhogen — doet niets, hij bindt niet; (b) isotrope diffuus-transpositie
(Erbs-splitsing + POA) — ruilt de bias om, schiet door op heldere dagen; (c) clearness-afhankelijke factor per kt-bucket —
out-of-sample slechts ~1% MAE-winst (46%→45%), want de uurfout wordt gedomineerd door ruis + de stralingsforecast-fout zelf.
Conclusie: globale factor houden. De factor-logregels ("Historical performance factor" met realized/forecast-totalen,
"Performance factor applied") staan sinds v1.0.144 op Warning (vuren 1×/dag).

## Belangrijke mechanismen (huidige staat)
- **ControlModeService** — `ControlMode` = SessyWeb/Manual/Charged/Provider, prioriteit leverancier > Charged > manual > wij. `WeMayDriveTheBatteries` is de enige schrijf-conditie op de hardware; **manual override = SessyWeb die een ander plan schrijft**, geen andere bestuurder. Lezers zijn ongeguard (`GetScheduleAsync`, identieke GET voor beide richtingen). Weigeren logt op Warning.
- **Anti-flapping** (state machine) — `MinimumModeDwell` (120 s) in `EnergySystemStateMachine`, klok uit `EnergySystemInput.Now` (`DateTime.MinValue` = geen klok in de snapshot). Asymmetrisch: wissel naar een minder actieve modus mag altijd direct; her-inschakelen/wisselen tussen even actieve modi zit de dwell uit. **Naar HoldReserve gaat altijd direct** (v1.0.150; blijft op NOM, geen strategie-wissel); terug wacht de dwell. Guards: `GuardHolds(...)` met `GuardReleaseFactor = 4`. `SelectIdleMode` (ZNH ↔ Disabled op netto-last en cycle cost) staat sinds v1.0.150 uit (`RemapIdleModes = false`, code blijft staan): ZNH en Disabled lopen zoals gepland. Deadband schaalt mee: `MinimumUsefulEnergyWh(capWh) = max(25, capWh × 0,005)`.
- **QuarterlyFactsService** — de enige plek waar een gemeten kwartier wordt samengesteld, één gezaghebbende tabel per grootheid: net ← `EnergyHistory` (delta, geklemd, per `MeterId`), zon ← `InverterMeasurements`, huisbelasting ← `Consumption`, batterij ← `QuarterlyMeasurements`, prijzen ← `EPEXPrices` + `Taxes`. Drager = `MeasurementView`. Twee regels: een totaal mag nooit begrensd worden door de tabel van een ándere grootheid (`GetDataRangeAsync` bepaalt het venster over álle bronnen); duplicaten in `QuarterlyMeasurements` worden bij lezen ontdubbeld (`GetAsync` groepeert op `Time`, hoogste `Id` — de unieke index staat nog niet terug).
- **Statistiek-venster** — `Settings.StatisticsFromDate` klemt de startdatum; `EnergyStatistics.ClampedByStatisticsFromDate` + `EffectiveStart/End` maken zichtbaar wanneer dat bijt. Een "komt niet overeen"-melding tussen twee pagina's is drie keer op rij een venster-verschil gebleken, geen rekenfout — reken eerst uit wélk venster het getoonde getal oplevert.
- **Sessy zonbron** — provider-key `"Sessy"` in `PowerSystems:Endpoints` (geen aparte sectie, geen eigen IP: leest `BatteryContainer`). Leest `PowerStatus.RenewableEnergyPhase*` van álle batterijen (of een subset via `Endpoint.Batteries`) en telt op; `InverterMaxCapacity` klemt de som en telt overschrijdingen. `InverterMeasurementWriter` schrijft de historie. De binnenste `Endpoints:Sessy`-sleutel is een label (`InverterId`), geen batterijnummer.
- **Charged handoff** — op de overgang naar Charged één POST: `ProfitMaximization` → ROI, overige strategieën → ECO (`BatteriesService.MapsToRoi`). `SetActivePowerStrategyAsync(handover: true)` omzeilt de schrijfguard; vuurt één keer, niet elke cyclus. Zit binnen `#if !DEBUG`.
- **ChargedScheduleService** — haalt altijd het niet-uitvoerende schema op (`GetScheduleAsync`), max 1×/5 min; de grafiek tekent het als gestippelde schaduwlijn. Toggle `ShowOther` in de grafiekheader (default uit).
- **Config-hardheid** — de ingebakken `appsettings.json` wordt uit de bronnen verwijderd (`RemoveBuiltInAppSettings`) + `CopyToPublishDirectory=Never`; config komt uitsluitend uit `$CONFIG_PATH`, ontbreekt die dan faalt de app zichtbaar. `PowerSystemsConfig.Endpoints` / `SessyP1Config.Endpoints` / `SessyBatteryConfig.Batteries` zijn niet-nullable met lege dict als default. Config-/`secrets.json`-restanten **declareren geen apparaat**: `IsConfigured` (= `BaseUrl` gevuld) is de gate, overgeslagen sleutels staan bij naam in Tips & Checks.
- **SystemCapabilitiesService / HasSolar** — één definitie of er zon is (minstens één omvormer). De UI verbergt zon-afhankelijke kaarten, menu-items en zinsneden als er geen zon is. Precedent: `HeatPumpIsConfigured`.
- **Tips & Checks** — `ConfigurationCheckService` draait de checks periodiek en houdt een samenvatting vast (`EnsureSummaryAsync`, 5 min, single-flight); een notificatiestip (rood/oranje) staat op het menu-item en de tab Settings, aangestuurd via `data-sessy-badge` (Radzen overschrijft een meegegeven `class`).

- **(Ont)laden via P1 grid target** (in productie sinds de P1-ombouw; vervangt het oude batterij-setpoint-pad). Batterijvermogen loopt via de grid target op de P1-meter (`P1MeterService.SetGridTargetAsync`, `POST /api/v1/meter/grid_target`); de batterijen draaien in NOM, want alleen dan volgt de Sessy de P1-grid-target — die balanceert het vermogen automatisch over alle batterijen en volgt het echte huisnetto realtime. In NOM houdt de Sessy `net = grid_target`, dus `batterij = huisnetto − grid_target` (`huisnetto = P1net + batterij`, per cyclus herrekend). Tekens: grid target import +, export −; `P1Details.PowerTotal` import +, export −; batterij ontladen +, laden −. Mode-mapping: Charging → NOM + `grid_target = huisnetto + P`; Discharging → NOM + `huisnetto − P`; ZeroNetHome → NOM + `grid_target = 0`; **HoldReserve → NOM + `grid_target = overschot > 0 ? 0 : huisnetto`** (`GridTargetCalculator.HoldReserveTargetW`; overschot = −huisnetto); **Disabled → Idle (`POWER_STRATEGY_IDLE`, sinds v1.0.143)** — zie de aparte Idle-regel hieronder. `GridTargetCalculator` (pure omrekening + clamp op nameplate), `GridTargetService` (5 s-refresher, deadband 50 W, `weDrive`-gate); in DEBUG geen POST, `LastComputedTargetW` toont de would-be waarde op `BatteriesPage`. FORCE_CHARGE/ZERO_EXPORT lopen gedwongen mee door dit pad. Instelbaar via setting **Battery control method** (`Settings.BatteryControlMethod`): *P1GridTarget* (default, hierboven) of *BatterySetpoint* — het oude pad: Open API + `StartCharging`/`StartDisharging` per batterij (aandeel naar nameplate, geen balancing, setpoint 1× per cyclus), ZeroNetHome blijft NOM met grid target 0, HoldReserve via Open API met setpoint = overschot (elke 5 s in `GridTargetService.ApplySetpointMethodAsync`); `ExpectedStrategy(mode, method)` verwacht dan `POWER_STRATEGY_API`. Open aandachtspunt: samenloop van `GridTargetService` en `InverterCurtailmentService` op de P1 (omvormer-kant van curtailment).
- **Day-ahead prijzen — merge over álle batterijen** (v1.0.143). `EPEXPricesService.FetchDayAheadPricesAsync` las vroeger alleen de eerste batterij (`Batteries.FirstOrDefault`). Batterijen lopen hun prijsvenster onafhankelijk bij: de één gaf nog gisteren+vandaag terwijl een ander al vandaag+morgen had — dan werd morgen nooit opgeslagen (géén exception, de call slaagt met een ouder venster) en bleef de grafiek "Prices are predicted" tonen. Nu worden de `energy_prices` van álle batterijen samengevoegd (`TryAdd` houdt de eerste die een slot biedt, latere vullen gaten zoals morgen); een trage/offline batterij blokkeert de rest niet (`continue` i.p.v. `return`). Gediagnosticeerd op Paul's systeem: .241 (Battery 1, de `FirstOrDefault`) liep een dag achter, .243 had morgen wél.
- **Voorspelde prijzen sluiten aan op de laatste bekende prijs** (v1.0.162). `ExpectedPriceService` vult morgen (vóór publicatie ~13:00) met het 60-daags gemiddelde per kwartier; dat negeerde het huidige niveau. 09-10: 23:45 €0,044, voorspeld 00:00 €0,173 → planner laadde 23:30–23:45 vol voor een verzonnen spread; Soft-marge (€0,05) ving dat niet. Nu `AnchorToLastKnown`: `voorspelling[i] = gem[i] + (laatst bekend − gem[0]) · e^(−uren/12)` (`AnchorDecayHours` = 12, vast). Backtest 522 dagen (EPEX-MAE €/kWh): 60 d-gemiddelde 0,0294, 14 d 0,0281, vandaag herhalen 0,0271, 60 d + niveau van vandaag 0,0298 (slechter), **aansluiten + 12 u uitdempen 0,0267** (6 u: 0,0275, 3 u: 0,0283); echte sprong 23:45→00:00 gemiddeld 0,014.
- **"Day-ahead prices fetched"-melding** (v1.0.143). Vuurde vroeger bij elke battery-fetch met prijzen — ook een venster met alleen vandaag — en daarna nooit meer door de `PriceSource != "Sessy"`-guard, dus je werd nooit geïnformeerd als morgen later binnenkwam. Nu alleen als de set morgen echt dekt, 1×/dag (`CoversTomorrow` + `_lastDayAheadNotifiedForDate`).
- **Runtime volgt het plan (v1.0.150).** `GetExecutableActionAsync`: guards (`GUARD_CHARGE_NO_ROOM`, `GUARD_CHARGE_TARGET_REACHED`, `GUARD_DISCHARGE_NO_ENERGY`) vallen terug op **HoldReserve** (`HoldAction`), niet meer op ZNH (dat dekte het huis uit net gekochte energie of onder de reserve). HoldReserve en ZNH/Disabled worden ongewijzigd uitgevoerd.
- **Herplannen op de reserve (v1.0.150).** `RebuildIfNeededAsync`: gemeten SOC ≤ `ReserveFloor` van het huidige kwartier + 5 Wh terwijl het plan ZNH zegt → geforceerde rebuild (reden "SOC at reserve while the plan covers the house"); de planner maakt dat kwartier dan HoldReserve. Max 1× per kwartier (`_reserveRebuildQuarter`, pas gezet na een gelukte solve), dus geen lus. Vangt verbruik boven de forecast binnen een kwartier af (06-10 12:15: 1525 W tegen 190 Wh forecast).
- **SOC-prognose herankert elke cyclus** (v1.0.144). `MilpServiceBase.WriteBackSocSimulationAsync` plakte binnen de solver-horizon de absolute `_planSocWhByTime` (van de laatste rebuild); tussen rebuilds liep de getekende "charge remaining"-lijn weg van de gemeten SOC zodra verbruik/zon afweek. Nu wordt per kwartier de solver-**delta** op de lopende, op de gemeten SOC verankerde `soc` toegepast (seed `prevSolverSoc` uit het kwartier vóór `nowQuarter`). Het vastgelegde plan (`_plannedSocByQuarter`) blijft de bron voor `GetCurrentSocDeviationPct` en de rebuild-trigger (`SocDeviationThresholdPct` = 20%), dus besturing ongewijzigd — puur de weergave.
- **Disabled draait op native Idle** (v1.0.143). `BatteryContainer.StopAll` → `Battery.SetActivePowerStrategyToIdle` (`POWER_STRATEGY_IDLE`) i.p.v. open API + setpoint 0. `BatteriesService.ExpectedStrategy(Disabled)` = `POWER_STRATEGY_IDLE`. Veilig: de schrijfguard keyt op eigenaarschap, niet op strategie. Geldt voor beide `BatteryControlMethod`s.
- **Notificatie-datum schuift al mee** (bevinding, geen wijziging). `NotificationDataService.IncrementAsync` zet bij een dedup-herhaling `CreatedAt = now` (+ `Count++`); de datum toont dus de laatst ontvangen. Dat de EPEX-"ok" "vast" leek op één tijd, komt doordat die 1×/dag vuurt, niet door een bug (bewijs: `backup-ok` Count=4 met datum = de back-up van vandaag).

## Valkuilen / eenheden
- `SolarPowerPerQuarterInWatts` is **Wh per kwartier** (`=> SolarPowerPerQuarterHour * 1000.0`), niet W. Met W×0,25 gerekend lijkt de zonforecast een factor 4 te laag.
- `Consumption.ConsumptionWh` is **Watt gemiddeld over het kwartier**, niet Wh. Conversie op één plek: `MeasurementView.ConsumptionKWh`.
- `QuarterlyMeasurement.BatteryMode` is óns uitgevoerde mode (incl. guard → HoldReserve), niet de hardware-keuze.
- Grafiekreeksen altijd een eigen `Stroke`/`Fill` geven: zonder kleur pakt Radzen het standaardpalet op volgorde, en voorbij het eind daarvan tekent hij geen lijn (v1.0.151: "Est. consumption" zwart in de legenda, onzichtbaar na de extra HoldReserve-band).
- Bestanden die open staan in Visual Studio: extern weggeschreven wijzigingen kunnen bij een build/opslaan door de VS-buffer teruggezet worden. Na wegschrijven buiten VS om altijd op schijf nacontroleren.
- Prijseenheden: ENTSO-E EUR/MWh ÷1000, Sessy ÷100000 → EUR/kWh; ENTSO-E levert PT60M (uur) → `ExpandToQuarters` (een prijs is een tarief, niet delen).
- `consumption = solar + net + battery` — een ontbrekende term wordt stil als verkeerd verbruik opgeslagen; daarom de `SolarIsMeasurable`-gate en `null`-in-plaats-van-partiële-som.
- **`SessyWeb.Services.PlanExplanationService` spiegelt de beslislogica van de planner** (baseline export-vs-opslaan-drempel, arbitrage, de betekenis van elke mode/`ActionMode`, en de prijs/round-trip/cyclus-afwegingen). Verandert de planner — nieuwe mode, andere drempel, andere prijsopbouw — pas dan **óók** deze class aan, anders beschrijft de plan-uitleg gedrag dat de planner niet meer volgt. (Sinds v1.0.126 staat die uitleg in de Chart Guide-popup, `ChartHelpComponent` met parameter `PlanWhy`; de losse "Why this plan?"-knop is weg.)

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
11. **Solar-opbrengst ophalen via de Sessy API.** Nu komt de gemeten zonproductie van SolarEdge
    (`InverterMeasurements`, `ProviderName="SolarEdge"`). Onderzoeken of de werkelijke opbrengst ook
    rechtstreeks via de Sessy kan (`GET /api/v1/power/status` → `renewable_energy_phase*`, som over de
    batterijen), zodat de SolarEdge-afhankelijkheid kan vervallen. Hangt samen met Openstaande punten 7
    en 9 (de Sessy-zonbron is gebouwd maar nog niet tegen een referentie bevestigd, en meet mogelijk
    alleen batterij 1).
12. **De accept-guard van de speculatieve solve staat uit bij negatieve winst.** `ObjectiveEur` is
    verwachte winst (hoger = beter), maar `RebuildIfNeededAsync` wijst alleen af bij
    `previousRate > 0 && newRate <= previousRate`. Op een netto-kosten-dag (objective negatief) wordt
    dus élke speculatieve solve geaccepteerd, ook een slechtere — 06-10 12:45: −3,53 → −3,64 EUR, het
    lege-batterij-plan verving het 12:30-plan mét middaglading. Niet blind `previousRate > 0` weghalen:
    na een onverwachte drain is de nieuwe solve legitiem slechter (minder energie), en dan mag het oude,
    onhaalbaar geworden plan niet blijven staan. Vergelijking moet rekening houden met het verschil in
    start-SOC.
13. **(Grotendeels opgelost v1.0.149–150.)** HoldReserve op de reserve, ZNH alleen bij volledige dekking,
    guards → HoldReserve en de rebuild-trigger op de reserve. Restrisico: binnen ~1 cyclus (60 s) na het
    bereiken van de reserve dekt ZNH nog door. Oorspronkelijk:
    **De runtime levert onder de reserve door.** De nachtreserve (`MinSocWh`) bestaat alleen in de
    planner; in NOM dekt de Sessy elke last tot de batterij leeg is. 06-10: onverwachte ~2,5 kW-lasten
    (00:15-01:15 en 12:15-12:45, forecast ~400 W) trokken de batterij in goedkope uren (€0,31) leeg tot
    0 Wh, vóór een avondpiek van €0,62. Opties: bij SOC ≤ reserve naar Idle i.p.v. doorleveren, of in
    goedkope uren met een dure piek in het vooruitzicht grote onvoorspelde lasten van het net laten komen.
    (De planner-deadlock die daarop volgde — DisCap 0 bij SOC 0 blokkeerde kandidaat B — is opgelost met
    `EmptyBatteryDeadlockTests`.)
14. **`ReserveFloor` veronderstelt een met SOC dalende laadcurve.** De zonbijdrage wordt gelezen bij
    `F[t+1]` (boven de werkelijke start-SOC); dat is alleen conservatief als laden bij hogere SOC niet
    sneller gaat. Een gemeten `ChargeCapability` met een (ruis)lage lage-SOC-bin kan de bijdrage
    overschatten → de baseline haalt een latere reserve dan net niet; niets repareert dat (het kwartier
    wordt alleen HoldReserve). Kleine hoeveelheden, alleen bij berekende reserve.
15. **Restpunten uit de review van v1.0.150 (laag).** Geen plan voor het huidige kwartier → ZNH (bij
    opstart). Geen P1-meting → `GridTargetService` post niets; in HoldReserve blijft het vorige target
    (bv. 0) staan en dekt de batterij het huis. De planner kan laden én ontladen in hetzelfde kwartier
    plannen; runtime doet alleen het laden. `coverCap` rekent met de gemeten (mediaan) capability,
    hardware kan meer. De DP-planner levert `ReserveFloorKWh` 0 → reserve-trigger vuurt daar nooit.
    De `trace` (`ExplainWhyNotSold`) draait op de scratch van vóór `RecoverHouseCover`.
16. **(Opgelost v1.0.152: HoldReserve-uitleg, "battery off" zonder export, shift-notitie bij verkoop; `Is()` negeert spaties.)** **`PlanExplanationService` kende HoldReserve en Candidate F niet.** Valkuil hierboven: die class
    spiegelt de planner. Bijwerken.
17. **(Opgelost v1.0.152.)** **`SETTINGS.md` miste `ShiftDischargeEnabled`** ("Allow moving discharge between quarters",
    v1.0.148) — in strijd met de afspraak onder Repository conventions.
18. **Tooltip grafiek.** "Requested" vs "Expected" tonen; "Actual power" alleen bij kwartieren in het verleden.
19. **Per batterij meten.** SOC/vermogen/status per batterij per kwartier vastleggen + kolom in het Throttling
    report (Statistics, v1.0.158). Basis voor een melding aan Sessy.
20. **Ontladen per SOC-band modelleren.** Nu één plateau (~3,65 kW); boven 80% SOC levert de bank 4,0–4,3 kW.
    Pas na punt 19.
21. **Opruimen.** `TmpEmptyProbeTests.cs`, `TmpGuardProbeTests.cs`, `tmp_guard_probe_output.txt`, de losse
    DP-schaduwplanner. Optioneel: migratie die `NightReserveCapPct` / `UseCalculatedNightReserve` /
    `ReserveSafetyFactor` dropt (dan ook de dode code onder "Minimum reserve").
22. **Vinger aan de pols: HoldReserve bij hoge inkoop.** HoldReserve springt het huis niet bij als de energie
    voor de piek bewaard wordt; bij hoge inkoop (bv. 17:45–18:30) is bijspringen mogelijk iets beter. Nu laten.
23. **Zelf testen (Paul): "Setpoint per battery"** tijdens ontladen, om per batterij de levering te zien.
24. **Na uitrol v1.0.160:** controleren of de laadsessie de geplande SOC haalt (laadstaart-fix).

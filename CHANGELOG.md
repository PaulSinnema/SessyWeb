# Changelog

What changed per version, from a user's point of view. The version is the one in
`SessyCommon/AppInfo.cs`, shown in the header and recorded in the `AppVersions` table, so the
database tells you which builds have run against it.

Engineering rationale — why a thing was built the way it was, what was measured, what was tried and
rejected — lives in `CLAUDE.md`. This file stays short: what changed, and what you notice.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Entries are grouped as
**Added**, **Changed**, **Fixed** and **Removed**. Versions before v1.0.78 are documented in
`CLAUDE.md` and the git history.

## [v1.0.118] — 2026-09-02

### Fixed
- **After a restart the plan chart now appears in seconds instead of after about a minute.** On a
  cold start the prices and battery status are not ready on the very first control cycle, so the
  planner skipped that cycle — and then waited a full 60 s before trying again. It now retries every
  5 s until the first plan is built, after which it settles back to the normal 60 s control cycle.

## [v1.0.117] — 2026-09-02

### Changed
- **During expensive hours the battery now exports surplus solar instead of always storing it.**
  The plan weighs selling the surplus now against what keeping it for a later quarter is worth, and
  when selling wins it leaves the battery off (grid strategy "API") so all solar flows to the grid,
  rather than charging it in ("Net zero"). This shows up mainly on sunny days with an expensive
  morning peak and cheaper hours later. When storing is worth more — the usual case — nothing
  changes. Exporting a surplus quarter now also shows as "Disabled" rather than "Net zero", so the
  plan reflects what the battery is actually doing.

## [v1.0.116] — 2026-09-02

### Fixed
- **The saved plan line still dropped battery self-consumption when read back from the database.**
  v1.0.115 fixed the dashed overlay, but the main plan line rebuilt charge/discharge from the stored
  mode text, which only knew "Charging"/"Discharging" — so a plan read back from the database showed
  every self-consumption (ZeroNetHome) quarter as nothing, while the live plan for the same quarter
  showed it correctly. The plan now stores charge and discharge power as two separate values and the
  dashboard reads them directly, so the live and saved plan lines match. Existing saved plans are
  back-filled automatically on first start.

## [v1.0.115] — 2026-09-02

### Fixed
- **The plan comparison line hid battery discharge that only covers the house.** A quarter where the
  battery powers the house (self-consumption, mode ZeroNetHome) really does discharge, but the
  dashed "other plan" overlay drew it as nothing — so a plan that discharges several kWh through the
  evening could look like it did nothing. The overlay now draws that self-consumption the same way
  the main plan area already does.

## [v1.0.114] — 2026-09-01

### Fixed
- **The next day's plan could sell nothing at all even when buy/sell prices left clear room for
  profit.** The plan for a quarter left in `ZeroNetHome` mode always showed 0 W, even when the
  underlying SOC path moved — Charge/Discharge quarters were unaffected. The stock-discharge floor
  (selling energy already in the battery) also charged the future replacement energy price on top
  of the wear cost, on every quarter, which priced out almost all of a day's trading; that floor is
  now the wear cost of the replacement cycle alone, grossed up for its round trip.

### Changed
- **Internal — `BatteryGreedyPlanner` split into focused, documented methods.** The single ~680
  line `Solve` method is now composed of small, individually-summarized steps (context setup, the
  baseline self-consumption pass, each arbitrage candidate, the trace explanation, and plan
  reconstruction). No behavioural change beyond the floor fix above — replayed plans are identical
  where the floor value is unchanged.

## [v1.0.113] — 2026-09-01

### Fixed
- **Internal — the shadow planner was scoring below the live planner (a measurement bug).** Its
  cost-to-go is now read with linear interpolation between SOC grid points, and it re-picks each
  action from the real (continuous) battery level instead of a rounded lookup, so the comparison no
  longer throws away value at the grid edges. The logged difference is again a fair upper bound.

## [v1.0.112] — 2026-09-01

### Changed
- **Internal — a shadow planner now measures how much a smarter planner could earn.** A
  dynamic-programming planner runs alongside the existing one on the same inputs and logs the
  difference in expected result; it does not change how the batteries are driven. It only gathers
  evidence for whether a future planner upgrade is worth building.

## [v1.0.111] — 2026-08-31

### Changed
- **Handing control to Charged now clears the grid target.** When Charged takes over, SessyWeb sets
  the P1 grid target to zero instead of leaving its last value behind, so a stale target no longer
  keeps steering the batteries after the handover.

### Removed
- **The period selector on the Energy statistics page.** That page is a lifetime overview — payback,
  return on investment, total savings — where picking a single day or week says nothing. It now
  always shows the whole history.

## [v1.0.110] — 2026-08-31

### Changed
- **The batteries are now steered through the P1 meter's grid target instead of a direct power
  setpoint.** To charge or discharge, SessyWeb keeps the batteries on the Zero-net-home strategy and
  sets a grid target on the P1 meter; the meter then drives the batteries to that target. The wanted
  power is recomputed against the live house load (consumption minus solar) every few seconds, so the
  batteries keep following the plan as consumption and solar move. A P1 meter is now required to steer
  the batteries — without one, Tips & Checks reports it as an error.

### Added
- **A "Grid target (P1)" card at the top of the Batteries page.** It shows, in watts, the grid target
  SessyWeb is aiming for (import positive, export negative), so you can see the steering at work. In a
  debug build it shows the would-be value without sending anything to the hardware.

### Removed
- **The per-battery "Setpoint requested" line** on the Batteries page. That value is no longer set now
  that steering runs through the grid target; the battery's own "Setpoint" reading stays.

## [v1.0.109] — 2026-08-13

### Added
- **The Energy flows card now says which period it is actually showing.** "Statistics from date" in
  the settings cuts off everything before it, whatever period you pick, and an incomplete first or
  last day is left out so the daily averages stay honest. That was invisible: with the setting on
  1 March, asking for 2026 gave 2110 kWh where the Consumption page showed 3389 kWh for the year,
  and the difference looked like a fault. The card now prints the window it used, and says so when
  the start came from that setting.

## [v1.0.108] — 2026-08-13

### Fixed
- **The statistics covered far less than the data you have.** Every figure was limited to the period
  for which battery telemetry exists — on this database that starts 13 May 2026, while consumption
  goes back to July 2025 — so household use read 1167 kWh where the Consumption page showed
  3389 kWh for 2026 alone. Each total is now summed over its own records across the whole period,
  and the period itself is the span every source together covers.

### Added
- **A period selector on the Energy statistics page.** It always showed the entire history and there
  was no way to change that, so its figures could not be compared with the Consumption or Solar
  power page, which do have one. Put both on the same period and they should now agree.

## [v1.0.107] — 2026-08-13

### Fixed
- **Household consumption on the Energy statistics page was too high.** It was recalculated there as
  grid import + solar − export, a formula with no battery term, so charging the battery from the
  grid counted as household use. It now shows the measured figure — the same one the Consumption
  page has always shown. On a sample week the old number read 102.6 kWh against a measured
  79.7 kWh, a 29% overstatement, and the difference matches the energy that went into the battery
  almost exactly.
- **Quarters recorded twice were counted twice.** A database index that should have prevented
  duplicates was lost in an earlier upgrade, and two background services write the same table. Any
  duplicated quarter doubled its battery energy and its consumption in every total. Duplicates are
  now collapsed on reading.
- **The Energy statistics page could stay empty while the Consumption page filled up.** The service
  that records meter readings gave up entirely when there was no weather data — the same fault
  fixed for consumption in v1.0.96, in the other service. Without those records the whole statistics
  page has nothing to show. Weather is now optional there, as it already was elsewhere, and a
  missing meter section says so in the log instead of failing silently.

### Changed
- **Every measured figure now has exactly one source.** Grid import and export come from the meter
  readings, solar from the inverter measurements, household load from the consumption
  measurements, battery state from the quarterly measurements — and all of it through one place, so
  the Solar power page, the Consumption page, the statistics and the financial results can no
  longer disagree. The meter calculation existed in four separate copies, one of which turned a
  meter reset into income on the financial page.

### Removed
- Unused code: the power-estimates service, an unused consumption query, and two data services that
  were injected but never called.

## [v1.0.106] — 2026-08-13

### Fixed
- **The battery no longer switches between "Net zero" and "API" dozens of times per quarter.** Three
  separate decisions could flip on measurement noise, and each flip rewrote the Sessy's power
  strategy: the charge and discharge guards compared a live state of charge against a fixed number
  of watt-hours, and the choice between Zero Net Home and switching the battery off compared the
  sign of a net load that is recalculated every cycle. All three now have to move a real distance
  before they change their mind, and a mode has to hold for two minutes before the opposite change
  is accepted — stopping is still immediate.
- **The threshold below which charging is "not worth it" now scales with your battery bank.** It was
  a fixed 25 Wh, chosen for three batteries; on a single battery that is inside the resolution of
  the reported state of charge, so it triggered on noise alone.

### Added
- **A notification dot on the Settings menu item when Tips & Checks has something to say** — red for
  errors, orange for warnings, in the top right corner of the gear, like the badge on a phone app.
  It sits on the icon rather than next to the label, so it is there with the menu collapsed to icons
  as well, and the Tips & Checks tab carries the same dot. Until now the checks only ran when you
  opened that tab, so a real problem could sit there unnoticed for weeks.
- **Tips & Checks reports both symptoms.** "Something else is changing the battery power strategy"
  when the battery keeps coming back on a strategy SessyWeb did not ask for — that is Home
  Assistant, the Sessy app or Charged writing as well, and it makes the plan and the hardware drift
  apart. "Battery mode kept changing" when the mode flipped repeatedly inside a single quarter, with
  the quarter and the number of changes.
- **The log now says the same two things.** Both are warnings, so they show up at the default log
  level, and both are written once per episode rather than every cycle. A battery whose strategy
  cannot be read at all is now a warning too — it used to be invisible.

### Changed
- **The control loop now always runs once a minute.** It ran once per *second* whenever nobody had
  the Charging hours page open in a browser, which multiplied every one of the above by sixty. The
  page still refreshes immediately when you open it.

## [v1.0.105] — 2026-08-12

### Added
- **PLANNER.md**: what the planner does, step by step, and which setting moves which part of it. The
  two passes and the four trades it scores, how the night reserve and the replacement cost are
  arrived at, every planning setting with its default and effect, the `appsettings.json` keys that
  reach the plan indirectly, the five models it fits from your own history, and a section on reading
  a plan that looks wrong. Linked from the README.

## [v1.0.104] — 2026-08-12

### Changed
- **README** corrected and filled in. It claimed prices and weather were the only outbound calls;
  there are four, and they are now listed with what each is for and when it happens. Also fixed: the
  planner does not look 72 hours ahead but as far as prices are published (24 to 48 hours), the
  arbitrage block is 0.2 kWh rather than 0.1, the Sessy CT clamps are named as a solar source
  wherever inverters are discussed, and the menu list matches the actual menu. Added: why you enter
  your coordinates twice, why a leftover battery in `secrets.json` keeps being contacted, and why the
  battery ends the last planned day low.

## [v1.0.103] — 2026-08-12

### Added
- **Current Plan** on the Statistics page shows the night reserve, as a percentage and in kWh, and
  says whether it was learned from measured nights or set by hand. It is the number that decides how
  much the plan may sell into the evening, and it was not visible anywhere.

### Fixed
- The plan ran the battery down to almost empty at the end of the last day it can see. Prices are
  known only through tomorrow, and the reserve for "the coming night" was counted up to that edge —
  so the last evening reserved a few hundred watt-hours instead of a night's worth, and the very
  last quarter reserved nothing at all, with a full night still to come. Where the view is cut off
  by the horizon rather than by the next sunrise, the measured night consumption now sets the floor.
  Only the final day changes; today and tomorrow morning plan exactly as before.

## [v1.0.102] — 2026-08-12

### Fixed
- The **Statistics** page stayed empty and threw when *Statistics from date* was left blank. Without
  that date nothing limited the period, so the page asked for everything and the meter query tried to
  look a quarter of an hour before the beginning of time. Leaving the field empty now simply means
  "all data", as it always should have.

## [v1.0.101] — 2026-08-11

### Fixed
- The solar source was reported as unreachable every night. Nothing reads the panels after sunset, so
  the health check saw a stale timestamp and called it an outage — **Tips & Checks** warned about a
  failure that was simply nightfall. Availability is no longer judged outside daylight.
- That warning also said "inverter" when solar is measured through the Sessy batteries, sending you
  off to check hardware that is not part of the setup. It now names the source you actually use.

## [v1.0.100] — 2026-08-11

### Added
- The Sessy solar source can be told **which batteries** carry the CT clamps, with an optional
  `Batteries` field on the endpoint. Leave it out and every battery is read and added up, which is
  right when only one has clamps. Name a subset when several Sessys see the *same* clamps, otherwise
  that production is counted twice. It takes battery keys from `Sessy:Batteries`, not addresses.
- **Tips & Checks** names any battery in that field that does not exist. A selection matching nothing
  reads no solar at all rather than quietly falling back to every battery.

## [v1.0.99] — 2026-08-11

### Added
- **Solar can now be measured through the Sessy batteries** instead of an inverter. Every Sessy
  already reports what it sees on its CT clamps; that reading was only ever displayed. Configure it
  with the provider key `Sessy` under `PowerSystems` and the Solar page, the statistics, the forecast
  and — the point of the exercise — the daytime consumption figures all start working without a
  readable inverter (issue #4). See the README for the configuration.
- **Tips & Checks** covers the new source: both sources configured at once, no curtailment while the
  Sessy is the source, no production seen for over an hour of daylight (the CT clamps are not around
  the PV group), and readings above the configured array capacity.

### Changed
- Configuring both `Sessy` and an inverter now uses only the Sessy. They measure the same panels, so
  running both would count every Watt twice.
- At negative prices the battery now simply follows the plan when the solar source cannot be
  throttled. Previously the same branch charged at full power from the grid *because* it assumed the
  inverter had been switched off — an assumption that does not hold for a read-only source. Nothing
  changes for an inverter, including an unreachable one.

## [v1.0.98] — 2026-08-11

### Fixed
- An inverter that is configured but unreachable reports **0 W**, and consumption counted that as
  darkness — so the household figure was short by the entire solar production without a single
  error. Those samples are now skipped instead of stored wrong, with one log line when it starts.

### Added
- **Tips & Checks** now explains why the Consumption page is empty during the day while it fills up
  at night (issue #4). Consumption is solar + grid + battery; with no inverter configured the solar
  term is 0, so every quarter in which the house exports comes out negative and is discarded. The
  check counts the discarded quarters and names the cause.
- The log line for a discarded quarter now says *why* it was discarded instead of only that it was.

## [v1.0.97] — 2026-08-11

### Fixed
- The navigation menu folded back to icons on every click, so expanding it never lasted longer than
  one page (issue #2). It now stays as you left it.

### Added
- A **User interface** tab under Settings, with the switch for that menu behaviour. Turn it off to
  get the old click-to-collapse back — useful on a phone, where the expanded menu covers the page.

## [v1.0.96] — 2026-08-10

### Fixed
- The **Consumption** page stayed empty when the weather feed was not configured or could not be
  reached. Weather is stored next to consumption but is not what is being measured, so it no longer
  stops recording — quarters are stored without weather values until the feed returns.
- Without weather the planner was told the house needs **0 W**. The monthly energy profile from
  Settings is now used as the fallback it was always meant to be.
- A quarter in which the P1 meter or a battery could not be read was recorded as 0 W instead of
  being skipped, quietly pulling the average down. Those samples are dropped now.
- With no P1 meter configured nothing was recorded and nothing was logged. That case now says so.

## [v1.0.95] — 2026-08-10

### Added
- **Tips & Checks** now covers the things consumption recording depends on: the weather feed
  (`WeerOnline`), the P1 meter (`Sessy:Meters`), the batteries (`Sessy:Batteries`) and the
  consumption history itself. A missing API key or an entry without a `BaseUrl` used to break
  recording with nothing on screen saying so — the **Consumption** page simply stayed empty. It now
  says which part is missing and what it costs you, and flags recording that has stopped or is
  dropping quarters.

## [v1.0.94] — 2026-08-10

### Fixed
- The planner assumed the batteries charge far slower than they do, and left energy unsold in the
  evening because of it. The charge taper is fitted on the few quarters that recorded an untapered
  request — on this database 223 of 7135, all from one hot spell — and predicted 2.3 kW at 80%
  state of charge. Measured on every charging quarter the bank accepts far more. The planner now
  also reads a floor measured straight in watts and never plans below what the batteries have been
  seen to accept. Replayed on a recorded plan this moves the evening from **6.6 kWh sold to
  14.2 kWh**, with the battery ending at its reserve instead of 8.3 kWh above it.

### Added
- **Tips & Checks** reports what the planner believes about charging: the taper, the measured
  floor, and how much of the state-of-charge range the measurements cover. It warns when the two
  disagree materially, so a taper drifting away from reality is visible instead of silent.

## [v1.0.91] — 2026-08-10

### Added
- Planner diagnostics. Set `SESSY_RECORD_SOLVE_INPUTS=1` and every plan rebuild writes the exact
  input it solved on — prices, battery spec, options and SOC bounds — as a JSON file in the export
  directory (the last 20 are kept). A plan that looks wrong can then be replayed exactly instead of
  reconstructed. Off by default; nothing is written without the variable.
- `PlannedQuarters` records the two planner inputs that could not be derived afterwards: the net
  household load it planned against and the reserve floor it had to stay above.

## [v1.0.87] — 2026-08-10

### Changed
- The timezone now lives only in the database, set on the **Settings** page. `Timezone` has been
  removed from the `ManagementSettings` section of `appsettings.json`; a new database starts on
  `Europe/Amsterdam` until you change it in the UI.

### Fixed
- Two startup timestamps — the backup taken before a migration, and the version stamp in
  `AppVersions` — were written in the timezone from `appsettings.json` on **every** start, because
  they run before the database settings are loaded. When the two sources disagreed, those rows were
  hours off from the rest of the application. Startup now reads the stored timezone first.

## [v1.0.86] — 2026-08-10

### Fixed
- Editing `appsettings.json` while the app runs did not reach the **Statistics** page: it read the
  configuration once at startup and kept that copy, so adding or removing a section (a heat pump,
  an inverter) only showed up after a restart. The page now rebuilds itself when the file changes,
  and the cached seasonal averages are dropped with it. The same freeze is gone from Tips & Checks,
  the battery API client, the solar forecast, the SolarEdge cloud fallback and the weather service.

## [v1.0.85] — 2026-08-10

### Added
- **Tips & Checks** now warns when an investment is counted in the payback period while the thing
  that produces its savings is not configured: a heat pump investment without a `HeatPumpConfig`
  section, or a solar investment without an inverter under `PowerSystems`. The cost keeps counting
  and the savings read €0/year, which stretches the payback period with nothing on screen saying
  why.

## [v1.0.84] — 2026-08-10

### Added
- This changelog. Kept from here on, one entry per version bump.

## [v1.0.83] — 2026-08-10

### Changed
- The UI now leaves out what your installation does not have. Without a solar inverter configured,
  the **Solar power** menu item is hidden and the page explains what to add instead of drawing empty
  charts. In **Statistics**, the five solar figures (solar production, self-sufficiency,
  self-consumption, performance ratio, avg/peak daily solar) and the self-sufficiency card in Energy
  Flows are hidden — without solar they can only read zero.

## [v1.0.82] — 2026-08-10

### Fixed
- Credentials left in `secrets.json` for a battery that was removed from `appsettings.json` created
  a phantom battery with no address, which then failed every poll with
  `Could not get power status after 3 tries for battery 2`. Secrets now augment a battery that
  `appsettings.json` declares; they no longer declare one. Entries without a `BaseUrl` are skipped
  with a warning that names the cause, and they no longer add capacity to the totals. The same
  applies to P1 meters.

## [v1.0.81] — 2026-08-10

### Removed
- `ChargedInControl` from the `ManagementSettings` section of `appsettings.json`. It bound to
  nothing; the setting that works is the **Charged in control** checkbox on the Settings page.

## [v1.0.80] — 2026-08-10

### Fixed
- A household without solar panels could not start: with no `PowerSystems` section the app threw a
  `NullReferenceException` before any service ran. Absent configuration sections are now empty
  rather than missing, throughout.
- With no inverter configured, curtailment reported "InverterMaxCapacity not set or wrong in config"
  instead of simply having nothing to throttle.
- With two P1 meters configured, only the last one was used.

## [v1.0.79] — 2026-08-10

### Changed
- The comparison checkbox above the charging-hours chart now works both ways. It always adds the
  plan that is *not* being executed: **Show Charged** while SessyWeb drives, **Show SessyWeb** while
  Charged drives. Whoever executes gets the filled areas, the other one a dashed shadow line.

### Fixed
- Under Charged control, the chart drew our plan as if it were being executed and left Charged's
  actual schedule off the chart entirely.

## [v1.0.78] — 2026-08-10

### Fixed
- The `appsettings.json` baked into the image was loaded on top of your own configuration file, and
  .NET merges configuration per key rather than replacing whole sections. Anything you did not
  override stayed alive: with one battery configured, the app kept polling batteries 2 and 3 at the
  image author's IP addresses. The same held for the P1 meter, the solar inverter, the weather
  location and the heat pump. Your file under `CONFIG_PATH` is now the only source, and the template
  is no longer shipped in the image.

  **Note:** if the configuration volume is missing, the app now fails visibly at startup instead of
  quietly running on the template's addresses.

# Changelog

What changed per version, from a user's point of view. The version is the one in
`SessyCommon/AppInfo.cs`, shown in the header and recorded in the `AppVersions` table, so the
database tells you which builds have run against it.

Engineering rationale — why a thing was built the way it was, what was measured, what was tried and
rejected — lives in `CLAUDE.md`. This file stays short: what changed, and what you notice.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Entries are grouped as
**Added**, **Changed**, **Fixed** and **Removed**. Versions before v1.0.78 are documented in
`CLAUDE.md` and the git history.

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

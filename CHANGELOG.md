# Changelog

What changed per version, from a user's point of view. The version is the one in
`SessyCommon/AppInfo.cs`, shown in the header and recorded in the `AppVersions` table, so the
database tells you which builds have run against it.

Engineering rationale — why a thing was built the way it was, what was measured, what was tried and
rejected — lives in `CLAUDE.md`. This file stays short: what changed, and what you notice.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Entries are grouped as
**Added**, **Changed**, **Fixed** and **Removed**. Versions before v1.0.78 are documented in
`CLAUDE.md` and the git history.

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

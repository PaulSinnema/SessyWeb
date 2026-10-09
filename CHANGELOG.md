# Changelog

What changed per version, from a user's point of view. The version is the one in
`SessyCommon/AppInfo.cs`, shown in the header and recorded in the `AppVersions` table, so the
database tells you which builds have run against it.

Engineering rationale — why a thing was built the way it was, what was measured, what was tried and
rejected — lives in `CLAUDE.md`. This file stays short: what changed, and what you notice.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Entries are grouped as
**Added**, **Changed**, **Fixed** and **Removed**. Versions before v1.0.78 are documented in
`CLAUDE.md` and the git history.

## [v1.0.163] — 2026-10-09

### Changed
- **One place for battery power limits.** The planner no longer uses the per-temperature throttle
  table; it relies on the measured charge and discharge power per state of charge, or on *Throttle
  fallback (%)* until those are measured. The Statistics chart *Battery throttle vs outside temperature*
  is replaced by *Battery power vs state of charge*, which shows exactly the limits the planner uses.
- **Charge power follows the outside temperature.** Measured: warmer means less charge power (about
  50 W per °C within a state-of-charge band). The planner now corrects the measured charge power to the
  forecast temperature of each quarter, within the temperature range seen so far. The chart shows the
  effect and charge power at the coolest and warmest measured temperature.

## [v1.0.162] — 2026-10-08

### Fixed
- **Predicted prices no longer jump at midnight.** Until tomorrow's prices are published, the predicted
  day now starts at the last published price and fades into the 60-day average over about 12 hours,
  instead of starting straight at that average. On a cheap day the planner no longer sees a large,
  invented spread just after midnight.

## [v1.0.161] — 2026-10-08

### Fixed
- **Red dot on Settings no longer sticks.** Tips & Checks only re-ran on navigation or when Settings was
  opened, so a problem that had already resolved kept its dot while you stayed on one page. The
  sidebar now re-checks in the background every few minutes and clears the dot on its own.

## [v1.0.160] — 2026-10-08

### Changed
- **Night reserve replaced by a simple Minimum reserve.** The calculated night reserve (learned from
  measured nights, with a safety surcharge and a bridge reserve for predicted prices) is gone. The
  batteries' BMS already keeps the cells from running empty, and the planner itself weighs covering
  the house at night against selling in the evening peak. What is left is one setting, *Minimum
  reserve (%)*, default 0: the planner never discharges below it. Whoever used the calculated reserve
  now has 0; a fixed reserve you set yourself is kept. Self-learning now only learns the future value
  discount. Settings, Statistics → Current Plan and the Planner analysis page follow.

### Fixed
- **Charging reaches its target at the end of a session.** Near the end of a charging session the
  command assumed a full quarter was still left, so the last part charged ever slower and the planned
  state of charge was not reached. The tail now charges at full power until the target is within one
  control cycle. If the batteries still fall behind, charging stops at the planned end and the next
  plan decides whether buying more pays.

## [v1.0.159] — 2026-10-08

No functional changes.

## [v1.0.158] — 2026-10-08

### Added
- **Throttling report** on the Statistics page, to see — and report to Sessy — how much power the
  batteries hold back. Over 30 days (default), 60, 90 days or all history: delivered versus
  requested power per 20% SOC band for charging and discharging (table and bar chart, as % of
  nameplate), how long a full charge and a full discharge take compared with nameplate power, and
  the loss per full cycle: efficiency (round trip over the same period) plus charge and discharge
  throttling and the total. Only quarters where SessyWeb itself asked for at least 90% of nameplate
  count.

### Changed
- **Throttling report explains itself.** The card now says on screen which quarters count, what the
  percentages and the full charge/discharge times mean, and that throttling in the loss per cycle is
  capacity you cannot use in a short price window, not lost energy. The info icon next to the title
  is gone.

## [v1.0.157] — 2026-10-08

### Added
- **Setting "Battery control method"** (Settings → Battery control). *P1 grid target* (default, as
  before): every battery runs in NOM and follows the grid target on the P1 meter; the Sessy firmware
  divides the power over the batteries, keeps them in balance and follows the house load in real
  time. *Setpoint per battery*: SessyWeb sets an Open API setpoint per battery again, as before the P1
  path — each battery gets a fixed share by nameplate, nothing balances them, and the setpoint changes
  once per control cycle. Zero Net Home stays NOM in both; Hold reserve follows the solar surplus every
  5 seconds in both. The Planner analysis page shows which method is active.

### Changed
- **Batteries page follows the control method.** With *Setpoint per battery* the Grid target (P1)
  block is hidden and each battery shows its *Requested setpoint* (what SessyWeb sent) again.
- **"Battery (actual)" on the Charging hours page is tidier.** No more cut-off text: the power reads
  "↑ charging 3688 W" / "↓ discharging … W" / "idle", the note is shortened to "via P1 grid
  target" and only shows with that method, and the Idle strategy reads "Idle".

## [v1.0.156] — 2026-10-08

### Changed
- **"Solar only" is now called "Hold reserve".** The mode mostly runs at night, when there is no
  sun: the battery keeps its energy and only stores surplus solar if there is any. Chart, badges,
  tooltips and the plan explanation use the new name; stored plans are migrated.
- **Heartbeat stays on while the control loop runs.** The heart on the Charging hours page
  disappears only after a missed beat (two control intervals without one), and shows right away
  when the page opens.
- **"Saved" on the Settings page disappears after 3 seconds**, so the next save shows it again.

## [v1.0.155] — 2026-10-08

### Fixed
- **The battery is filled and sold into the evening peak again.** From an empty battery the plan
  charged only about half (13:30-15:15 on 08-10) and sold from 17:30 at €0,27 while 19:00-21:30
  paid €0,34-0,38. Below about 20% SOC the batteries deliver less power, and the planner could not
  see that the battery has to stay full until the peak to sell there at full power. The planner
  now also builds a plan with the DP planner, which does see this, and keeps whichever scores
  better on the planner's own terms. On 08-10 the battery charges 12:30-16:00 and sells
  18:45-21:30 at full power.
- **No more promised discharge the battery cannot deliver.** An earlier sale could lower the SOC
  so far that a later planned quarter could no longer reach its power (up to 0,32 kWh in the
  replays). Such a sale is now refused.
- **The DP planner follows the same rules.** Measured charge power, the reserve floor, exact solar
  storage and house cover, and the store-or-export choice for solar. Charge power can no longer
  rise with the SOC, so the DP does not buy at full price to reach a faster bin.

## [v1.0.154] — 2026-10-07

### Changed
- **Main chart back to its familiar colours.** After v1.0.153 the main chart showed very
  different colours (pink, light blue). It uses its original fixed colours again, Solar only is
  green again there, and the Chart Guide swatches match the chart. Other pages keep the v1.0.153
  palette.

## [v1.0.153] — 2026-10-07

### Changed
- **One colour palette for the whole GUI.** Every page, chart, tooltip and badge now takes its
  colours from one set in `site.css`, so the same thing has the same colour everywhere:
  consumption is purple on every page (was blue on the Consumption page, the colour of the buying
  price), charging orange and discharging green also in the tooltip, the throttle chart and the
  Planner analysis badges, and the mode badges match the chart (Zero net home gold, Solar only
  teal). Solar only moved from green to teal so it no longer looks like discharging.
- **Readable on light and dark themes.** Line and text colours blend in the theme's text colour,
  so they keep their contrast on every theme. Yellow, cyan and green badges and buttons get dark
  text instead of white; the "now" line, the help close button and the temperature line no longer
  disappear on a light theme. The Chart Guide and the battery table follow the chosen theme
  instead of a fixed dark style, and the Chart Guide lists the series that were missing (cost
  basis, Solar only, throttle loss, Charged, plan history).

## [v1.0.152] — 2026-10-07

### Fixed
- **Plan explanation knows "Solar only".** The chart guide's "why this plan" now explains Solar
  only quarters (at the reserve, or energy kept for later), a battery that is off because its energy
  went to an earlier sale, and notes when a sale makes later quarters import for the house. Zero
  Net Home quarters are now also counted when the plan comes from the display fallback.

## [v1.0.151] — 2026-10-07

### Fixed
- **"Est. consumption" line visible again on the charging hours chart.** It had no colour of its
  own and took one from the chart's default palette; with the new "Solar only" band it ran past
  the end of that palette and was drawn without a line. It now has a fixed colour, and so does
  "Delta lowest price", which had the same problem.

## [v1.0.150] — 2026-10-07

### Changed
- **Execution follows the plan strictly.** Zero Net Home and Disabled quarters run exactly as
  planned; the runtime no longer swaps them on net load or cycle cost.
- **Charts show "Solar only"** as its own green band, and a quiet battery in a Solar only or
  Disabled quarter no longer counts as a plan deviation.

### Fixed
- **Zero Net Home only where the plan covers the whole house.** The plan labelled quarters Zero
  Net Home while it covered none or part of the house load there, but Zero Net Home covers all of
  it at runtime — in the 06-10 replay 1,16 kWh more than planned, ending below the reserve. Such
  quarters are now "Solar only", and house cover that arbitrage made possible afterwards is added
  back to the plan where the battery has room above the reserve for the rest of the horizon,
  dearest quarters first.
- **Battery stops at the reserve when the house uses more than forecast.** As soon as the measured
  SOC reaches the reserve during a Zero Net Home quarter the plan is rebuilt (within a minute) and
  the quarter becomes "Solar only", instead of covering the house below the reserve until the next
  quarter. The switch to "Solar only" is no longer held back by the 2-minute mode dwell.
- **Calculated night reserve: tonight's house load is covered again.** The plan kept the highest
  reserve anywhere in the horizon from the first quarter on — with a calculated reserve that is the
  full night reserve at the end of tomorrow — so the battery stayed idle overnight while tomorrow's
  sun refills it anyway. The house is now covered down to the reserve each later quarter still needs
  after the solar in between. A fixed reserve plans exactly as before.
- **Runtime guards hold the energy.** When a charge quarter finds the battery full or the target
  reached, or a discharge quarter finds nothing left above the reserve, the battery now goes to
  "Solar only" instead of Zero Net Home — which covered the house from energy just bought, or
  below the reserve.

## [v1.0.149] — 2026-10-07

### Added
- **New battery mode "Solar only".** Stores solar surplus when there is any (surplus > 0) and never
  discharges (surplus <= 0: the house imports). Runs on Zero Net Home (NOM) with a P1 grid target
  that follows the live net load every 5 seconds, so it reacts to the actual surplus, not the
  forecast. Shown as a yellow "SolarOnly" badge on the Planner analysis page.

### Fixed
- **Batteries no longer run below the reserve overnight.** On 06-10 → 07-10 the plan held the
  reserve (810 Wh) but labelled the quarters at the reserve Zero Net Home, which covers the whole
  house at runtime whatever was planned — the batteries went to 0% at 04:00. The planner now plans
  "Solar only" for every quarter in which the reserve is reached: the SOC sits on it, or covering
  that quarter's house load would cross it. Grid charging and export keep their own modes.

## [v1.0.148] — 2026-10-06

### Added
- **Spread column on the Planner analysis page.** Per quarter, the margin per delivered kWh of the
  energy that quarter buys or sells, paired by FIFO (the same layer bookkeeping as the cost basis):
  sale value (avoided buy price for the house, sell price for export) minus what that energy cost.
  A charge quarter shows the margin of the sales its energy ends up in; a discharge quarter the
  margin over the layers it empties, including stock from before the plan. Empty when nothing is
  paired within the plan.
- **Optional: sell stored energy in the peak instead of covering cheaper house load later.**
  New setting "Allow moving discharge between quarters" (Settings → planner, default off). Without
  it the plan assigns the battery to covering the house through the evening and night first, and
  nothing takes that back — on 07-10 18:45 (sell €0,396) stayed unsold while the same energy
  covered the house at ~€0,34 later. With it on, the planner moves a discharge between quarters
  (earlier or later) when that pays at least €0,01/kWh, counting the efficiency loss of low-power
  quarters exactly. A quarter whose house cover is moved away is planned as Off (the house imports),
  not Zero Net Home, which would drain the battery anyway. Side effect: the battery reaches the
  night reserve more often and earlier. Shown as "Shift discharge" in the Planner analysis parameters.

## [v1.0.147] — 2026-10-06

### Fixed
- **"Solar: Performance factor applied" no longer floods the log in debug builds.** Debug builds
  wrote it every cycle; it now logs only on a new day or when the factor changes, as in release.

## [v1.0.146] — 2026-10-06

### Fixed
- **Plan no longer overestimates charge power above ~40% SOC.** Measured over 15-09..06-10, the
  plan expected 4.7-5.4 kW there while the batteries sustained 3.2-3.9 kW, so charging delivered
  ~75% of the planned energy and the real SOC fell behind the forecast. The old estimate kept the
  highest momentary power reading per SOC band. The planner now uses the median power the
  batteries actually stored per quarter, per 10% SOC band, over the last 60 days (only quarters
  that requested full power and followed another charging quarter). Bands without enough data
  keep the old estimate. Effect: charging is spread over more cheap quarters instead of assuming
  power that never arrives.
- **Plan no longer overestimates discharge power.** Same cause on the way out: the discharge
  plateau was the highest momentary reading per SOC band (4.67 kW), while full-power discharge
  quarters delivered a median of ~3.7 kW, so full-power discharging reached ~82% of plan. The
  plateau is now the median of sustained full-request discharge quarters over the last 60 days, and
  the knee (where power starts to fall with SOC) is re-read on those medians: 20% instead of 30%.
  The plateau is only ever lowered by this, never raised.

## [v1.0.145] — 2026-10-06

### Fixed
- **Planner no longer gives up when the battery is empty.** When the battery reached 0 Wh on a day
  without solar surplus, the plan stayed idle for the rest of the day — no cheap charging, nothing
  left for the evening peak. Below the discharge knee the deliverable power falls to 0 at 0 Wh, and
  a single charge-then-discharge trade could never fit in one quarter.
- **Planner charges enough for the evening peak when SOC drops below the discharge knee.** Below the
  knee (~30% SOC) each quarter can only deliver a slice of any extra charge, so the planner credited
  extra afternoon charging with almost nothing and stopped at ~8 kWh, leaving the peak half-used. It
  now values a charge by everything it delivers over the following knee-limited quarters. On the
  06-10 13:00 plan: 11 kWh charged instead of 8, full power through the peak for longer.
- **Planned discharge no longer exceeds the measured discharge capability.** Some evening quarters
  were planned up to ~5% above what the batteries can deliver at that SOC.
- **Plan calculation much faster.** Below the knee the search crept on in ever smaller steps until
  its 5000-iteration safety limit.
- **No false "Something else is changing the battery power strategy" under Manual override.** The
  manual charge/discharge hours run through the open API (POWER_STRATEGY_API), while the check still
  expected the plan's strategy (NOM). The check is now skipped while Manual override is on. Now it ends normally (local test: 15 s → 1.2 s for
  seven solves).

## [v1.0.144] — 2026-10-05

### Changed
- **SOC forecast re-anchors on the measured SOC every cycle.** The forward "charge remaining" line
  used the solver's absolute SOC from the last rebuild, so between rebuilds it drifted away from the
  real SOC as soon as actual consumption or solar deviated from the forecast. It now applies the
  plan's per-quarter SOC deltas onto the live measured SOC each cycle, so the forecast starts where
  the battery actually is and shows the plan's intent from there. The committed plan stays the
  reference for the deviation metric and the rebuild trigger, so control behaviour is unchanged.
- **Solar performance factor is now logged at Warning level.** The daily "Historical performance
  factor" (realized vs forecast kWh, raw ratio) and "Performance factor applied" lines were at
  Information and therefore invisible at the usual log level. They now surface once per day, so the
  solar-forecast correction factor can be seen without lowering the log level.

## [v1.0.143] — 2026-10-05


### Fixed
- **Day-ahead prices are now merged across all batteries.** The price fetch read the schedule of
  only the first configured battery. When that battery's schedule window lagged a day (it returned
  yesterday+today while another battery already carried today+tomorrow), tomorrow's real prices were
  never stored, so the chart kept showing tomorrow as predicted even though the prices were available
  on another battery. The fetch now queries every battery and unions their energy prices, keeping the
  widest coverage; a battery that fails or lags no longer blocks the others.
- **"Day-ahead prices fetched" notification no longer fires without tomorrow's prices.** The
  notification used to fire whenever the batteries returned any prices, even a schedule that held
  only today — so it could announce success while the price chart still showed tomorrow as
  predicted. It now fires only when the fetched set actually covers tomorrow, and once per day, so
  the notification and the chart agree.

## [v1.0.142] — 2026-10-05

### Added
- **Planner analysis page (sidebar, developer-only).** A new page — second in the sidebar, shown only
  when **Developer options** is on — that analyses the current plan. It has a summary, a per-quarter
  grid (mode, price position, buy/sell, charge/discharge power, SOC, estimated solar, estimated
  consumption, net load, remark count) and an expandable detail per quarter with: why that mode was
  chosen, a motivation of why solar is or is not exported (storing for a dearer later moment versus
  exporting now), the relevant data and calculations, and improvement remarks where the plan looks
  sub-optimal (e.g. discharging cheap while a dearer hour is ahead, or keeping solar while exporting
  now would pay more). A footer lists every planner parameter that shaped the plan. Read-only and
  heuristic; built from the stored plan and current settings.

### Changed
- **Disabled mode now uses Sessy's native Idle strategy.** Previously Disabled was executed by
  switching the battery to the open API with a setpoint of 0 W. It now hands the battery to Sessy's
  own `POWER_STRATEGY_IDLE`, so it holds without an API setpoint. Behaviour is the same (no charge or
  discharge), but it is one native strategy instead of an API override.

## [v1.0.141] — 2026-10-03

### Added
- **Reconstruct a past solve-input (Plan dump tab).** Rebuilds the planner's input for a chosen
  period from the database — exact prices, net load and reserve floor from the stored plan, initial
  SOC from the measurement — and downloads it, so a plan that ran before recording was switched on can
  still be replayed. Approximate: the battery spec and planner options are the current ones, not as
  they were then.
- **Developer options toggle (Settings → Management → Diagnostics).** Off by default; hides the whole
  **Plan dump** tab until switched on.

## [v1.0.140] — 2026-10-03

### Changed
- **Default export directory is now `/SessyController/Data/exports`.** The old default `/data/exports`
  is not a mounted volume in the Docker container, so solve-input and query exports landed nowhere (or
  inside the container). The new default sits inside the mounted data volume. Existing installations
  keep their configured value — clear the **Export directory** field to pick up the new default, or set
  it to `/SessyController/Data/exports` by hand.

## [v1.0.139] — 2026-10-03

### Added
- **Solve-input recording is now a setting (Settings → Management, "Diagnostics").** The planner can
  write its exact solve input (prices, battery spec, options, SOC bounds) to the export directory on
  every rebuild, for replaying a plan that looks wrong. Previously only switchable via the
  `SESSY_RECORD_SOLVE_INPUTS` environment variable (which still forces it on). The number of files to
  keep is configurable too (default 20), and the export directory is editable here.

### Changed
- **Solve-input recording no longer auto-creates the export directory.** A missing or unmounted path
  used to be created silently inside the container, where the files are lost on restart. It now skips
  writing and raises a Tips & Checks warning instead, the same way the backup directory does.

## [v1.0.138] — 2026-10-03

### Added
- **Plan dump (Settings → Plan dump).** A new tab lets you pick a period and download a single JSON
  snapshot with everything needed to analyse a plan: planner settings, EPEX prices, taxes, planned
  quarters, actual quarters and measured facts. The file is built entirely in memory and offered for
  download — it is never written to disk. Handy for sending a bad plan over for analysis.

## [v1.0.137] — 2026-09-22

### Changed
- **Gas price fetching is now its own service.** It used to live inside `EPEXPricesService`; it now
  runs as a dedicated `GasPriceService` (same daily fetch, rate-limit handling and backoff), behind a
  new `IGasPriceService`. Purely a structural cleanup — no behaviour change.

## [v1.0.136] — 2026-09-22

### Fixed
- **Gas price fetch: rate-limit handled, no more quarter-hourly retries.** When the free Enever.nl
  feed hits its monthly token limit it answers with an error string instead of a price array, which
  used to throw a confusing "Array vs String" error and retry every quarter. It now reads the error,
  shows a clear notification ("token limit exceeded — resets on the 1st of the month"), and backs off:
  a token limit waits until the 1st of next month, other failures use exponential backoff (15 min
  doubling, capped at 6 h). A success clears the notification and resets the backoff.

## [v1.0.135] — 2026-09-21

### Fixed
- **A failed pre-migration backup now aborts startup instead of migrating without one.** The
  pre-migration backup guards against a bad migration, so if it fails (e.g. a misconfigured
  `DatabaseBackupDirectory`) the app logs a clear FATAL line and stops before migrating — rather than
  migrating without a safety copy. Fix `DatabaseBackupDirectory` (point it at the mounted
  `/SessyController/Data/Backups`) and restart.

## [v1.0.133] — 2026-09-21

### Added
- **Notifications — a generic, persistent message queue.** Any part of the app can raise a
  notification with a severity (Information, Warning, Error). They appear on a new **Notifications**
  tab in Settings, newest first and colour-coded per severity, and survive a restart. Filter by
  **Severity** and **Category**; delete one, delete all, or mark all read. The app raises them for:
  database backup success (Information) and failure (Error); day-ahead price fetch success with its
  source — Sessy or ENTSO-E — (Information) and failure (Error); weather fetch failure (Error); and
  gas-price fetch failure (Error). Both the nightly backup and the manual Backup button go through the
  same routine, so either reports the result; a failure clears itself once the operation succeeds
  again, and error notifications carry the root-cause message so you see what actually went wrong.
- **No duplicate notifications, with an occurrence counter.** A repeat of the same notification (same
  key and message) bumps the existing entry and shows a **×N** counter instead of adding a row, so a
  repeating failure never floods the queue; a different failure reason under the same source is kept
  as its own notification.
- **The Settings menu dot reflects notifications too.** It combines Tips & Checks and notifications
  with severity leading — red for any unread error, orange for a warning, nothing otherwise; the
  strongest wins.

## [v1.0.132] — 2026-09-21

### Fixed
- **Database backups no longer vanish silently on a mistyped path.** The backup directory is no
  longer created automatically: a typo in `DatabaseBackupDirectory` (for example `/SessyControler/…`
  with one `l`) used to be created inside the container's temporary storage, so `VACUUM INTO`
  "succeeded" and every backup was lost on the next restart. The backup now fails with a clear
  message when the directory does not exist, telling you to point it inside the mounted data volume.

### Added
- **Tips & Checks now warns about backup problems.** A failed automated backup, a missing backup
  directory, or a newest backup older than 48 hours is shown as an error on the Tips & Checks tab,
  instead of only being written to the log where nobody looks.

## [v1.0.131] — 2026-09-20

### Added
- **`SETTINGS.md` — a reference for every setting on the Settings page.** Each tab and field with its
  default and what it does, linked from the README. Going forward it is kept in sync as settings change.

## [v1.0.130] — 2026-09-20

### Changed
- **Internal — GitHub releases are now created automatically.** After the Docker image is published,
  a second CI job reads the version from `AppInfo.cs`, takes this file's matching section as the
  release notes, and creates the GitHub release (skipping if one already exists for that version).
  No user-visible change.

## [v1.0.129] — 2026-09-14

### Added
- **A custom date range on the Consumption page.** You can pick a start date and view consumption
  over a chosen window from it — the last 7, 30, 90, 180 or 365 days — with the total consumed kWh
  for that period shown alongside.

## [v1.0.128] — 2026-09-04

### Changed
- **The night-reserve settings now show only the field that applies.** The "Night reserve source"
  checkbox comes first, and below it you see the "Night reserve cap (%)" when Calculate from history
  is on, or the "Fixed night reserve (%)" when it is off — never both. The two are mutually exclusive
  (the checkbox picks which one the planner uses), so showing only the active one removes the earlier
  confusion about whether they were the same setting.

## [v1.0.127] — 2026-09-04

### Changed
- **The "Battery (actual)" header now explains why it says Zero Net Home while charging.** With
  grid-target steering the battery strategy stays Zero Net Home (NOM) and the P1 grid target does the
  charging/discharging, so the plan can read "Charging" while the hardware shows "Zero Net Home". The
  header now adds a small note — "charge/discharge via P1 grid target" — with a fuller explanation on
  hover, so the two no longer look contradictory.

## [v1.0.126] — 2026-09-04

### Changed
- **A fresh install now defaults to a fixed cycle cost of €0,04/kWh** (checkbox "Calculate from
  investments" off), instead of deriving the full wear cost from the investments. Existing installs
  keep whatever they have set; change it under Advanced planning parameters.
- **The safety surcharge now actually applies to a fixed night reserve.** With a fixed reserve the
  surcharge used to be swallowed by a cap, so it never raised the reserve and low-consumption nights
  could even hold less than the set percentage. A fixed reserve is now a firm floor with the safety
  surcharge on top (e.g. 10% reserve + 10% surcharge = 11% held), independent of the night forecast.

### Removed
- **The separate "Why this plan?" button.** Its explanation now lives inside the Chart Guide (the "i"
  button), so the whole-plan summary and the legend are in one place.

## [v1.0.125] — 2026-09-04

### Added
- **The cycle (wear) cost can now be a fixed amount you set yourself, or switched off entirely.**
  Advanced planning parameters gain a "Fixed cycle cost (€/kWh)" field (default 0) and a "Calculate
  from investments" checkbox. Leave the checkbox on to keep the wear cost derived from your battery
  investments as before; switch it off to use the fixed amount instead — set it to 0 to remove the
  wear penalty completely, so the planner trades on every profitable spread (more like Charged).

## [v1.0.124] — 2026-09-04

### Changed
- **Internal — renamed the planner's `ReplacementCostEurPerKWh` to `ReservationPriceEurPerKWh`.** The
  old name suggested battery replacement, but the value is a reservation/floor price for carrying
  energy past the horizon, not a wear cost. No behavioural change.

## [v1.0.123] — 2026-09-04

### Fixed
- **"Why this plan?" showed an impossible reserve (e.g. 21 or 41 kWh).** The end-of-window reserve
  was formatted with a stray code that dropped the decimal separator and appended a literal "1", so
  1,7 kWh read as "21" and ~4 kWh as "41". It now shows the real value with one decimal.

## [v1.0.122] — 2026-09-04

### Added
- **The night reserve can now be a fixed percentage you set yourself.** Battery control settings gain
  a "Fixed night reserve (%)" field (default 10%) and a "Calculate from history" checkbox. Leave the
  checkbox on to keep the self-learned reserve as before; switch it off to make the planner hold
  exactly the percentage you set. Lowering it lets the battery discharge deeper into the evening peak
  instead of holding energy back for the night.

## [v1.0.121] — 2026-09-02

### Changed
- **Internal — documented that the plan explainer must track the planner.** `PlanExplanationService`
  (behind "Why this plan?") mirrors the planner's decision logic; a keep-in-sync note was added to the
  class and to `CLAUDE.md` so it is updated whenever the planner changes. No user-visible change.

## [v1.0.120] — 2026-09-02

### Added
- **The plan now explains itself.** Hovering a quarter in the charging-hours chart shows why that
  quarter is planned the way it is — a plain sentence plus the numbers behind it (prices, kWh, how
  it ranks against the rest of the window, and the best later alternative). A new "Why this plan?"
  button above the chart opens a short summary of the whole plan: what it charges in the cheap hours,
  sells in the expensive ones, covers from the battery, and keeps as reserve.

### Fixed
- **The chart tooltip no longer disappears every refresh.** The recurring control-cycle refresh used
  to flash the busy spinner each time, which closed any open tooltip. The spinner now only shows on
  the first load; later refreshes update silently.

## [v1.0.119] — 2026-09-02

### Added
- **The container log is now visible live in Settings.** A new "Container log" tab shows the
  application's log stream in a scrollable window, updating as new lines arrive. Auto-scroll is on
  by default (with a checkbox to turn it off), a level dropdown filters the view (Trace … Critical,
  starting at Warning), and Copy / Export buttons put the shown text on the clipboard or download it
  as a file. The buffer keeps the most recent 1000 lines.

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

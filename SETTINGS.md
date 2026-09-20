# Settings reference

Every setting on the **Settings** page in the SessyWeb UI, tab by tab, with its default and what it
does. These take effect immediately — no restart. Values that live in `appsettings.json` instead (the
ones you set once at install) are covered in [README.md](README.md#configuration-reference); the
planner maths behind the planning knobs is in [PLANNER.md](PLANNER.md).

> **Maintenance:** when a setting on the Settings page is added, removed or changed, update this file
> in the same change (and its default here). This file is meant to stay a complete mirror of the page.

## Management Settings

### Location

| Setting | Default | What it does |
| --- | --- | --- |
| **Latitude** | — | Your latitude. Used to compute sunrise and sunset (the solar window). You enter coordinates a second time here on top of `appsettings.json`; keep them the same. |
| **Longitude** | — | Your longitude, same purpose. |
| **Timezone** | `Europe/Amsterdam` | The timezone all planning and statistics run in. Lives only in the database, set here — not in `appsettings.json`. |

### Solar

| Setting | Default | What it does |
| --- | --- | --- |
| **Annual production (kWh)** | — | Your panels' expected yearly yield. Feeds the solar forecast scaling and the statistics. |

### Battery control

| Setting | Default | What it does |
| --- | --- | --- |
| **Optimization strategy** | Profit maximization | How the planner weighs its goal: *Profit maximization* (trade for the best financial result), *Self-consumption* (favour using your own solar over exporting), *Balanced*, or *Battery saving* (cycle the battery less). |
| **Night reserve source** — *Calculate from history* | On | On: the night reserve is the self-learned **Night reserve cap** below. Off: the planner uses the **Fixed night reserve** below, giving you direct control. Only the field that applies is shown. |
| **Fixed night reserve (%)** | 10 | Fixed percent of capacity held back for the night, used only when *Calculate from history* is off. A firm floor with the reserve safety surcharge on top. Lower it to let the battery discharge deeper into the evening peak. |
| **Night reserve cap (%)** | 0 (= 33%) | Caps energy held back for the night, as a percent of capacity. 0 falls back to 33%. With self-learning on, the nightly fit overwrites this once enough nights are measured; an edit holds until then. |
| **Throttle fallback (%)** | 0 (= 80%) | Power cap used only until the throttle at the current temperature has been measured. Once samples exist the measured ratio takes over. Assuming no throttle would make the planner ask for power the battery cannot deliver. |
| **Round-trip efficiency fallback (%)** | 0 (= 90%) | Round-trip energy efficiency used until enough charging/discharging is measured — how much stored energy comes back out, which decides whether arbitrage pays at all. The planner derives the one-way efficiencies as its square root. Distinct from the throttle, which limits power. |
| **Charged in control** | Off | When on, SessyWeb hands driving to Charged and sends no commands itself. Untick it to let SessyWeb drive. |
| **Manual override** | Off | When on, you set the charging / discharging / Zero Net Home hours by hand (fields below) instead of letting the planner decide. |
| **Manual charging / discharging / Zero Net Home hours** | — | Only shown with **Manual override** on: the hours to force each mode. |

### Advanced planning parameters

A **Reset to defaults** button restores this whole block. See [PLANNER.md](PLANNER.md) for the maths.

| Setting | Default | What it does |
| --- | --- | --- |
| **Cycle cost source** — *Calculate from investments* | Off (use fixed) | On: the wear cost per kWh is derived from the battery investments (cost / capacity / cycles). Off: the planner uses the **Fixed cycle cost** below. |
| **Fixed cycle cost (€/kWh)** | 0.04 | Fixed battery wear cost per kWh, used when *Calculate from investments* is off. 0 disables the wear penalty entirely (trade on every profitable spread); higher makes the planner more cautious about cycling. |
| **Reserve safety surcharge (%)** | 10 | Added on top of the calculated night and bridge reserve — 10 means keep 10% more than the calculation asks for. Raise it if the battery regularly runs empty overnight. Also applied to a fixed night reserve as a firm floor. |
| **Planning horizon (hours)** | 0 (no limit) | Quarters beyond this many hours are ignored by the solver, so discharge cannot be deferred to a distant peak. Typical values 24 or 36; 0 uses every quarter with a known price. |
| **Use predicted prices in the solver** | Off | *Off*: published prices only — predicted quarters extend the horizon for night coverage but are never traded. *Soft*: predicted quarters traded with a risk margin, so only wide spreads act. *Full*: predicted prices trusted like published ones. |
| **Predicted price risk margin (€/kWh)** | 0.05 | Only shown with *Soft*. Raises the buy and lowers the sell price on predicted quarters by this amount, so a predicted quarter is only arbitraged when the gain comfortably beats the price uncertainty. |
| **Future value discount (%/hour)** | 0.3 | Time preference: how much less a quarter is worth per hour of distance when placing stored energy, so a distant peak must be clearly better to win. 0 turns it off (a single distant peak can claim the whole battery). Only affects planning, not reported profit. Raise it if you trade on predicted prices. Learned nightly when self-learning is on. |
| **Allow carry-forward past the horizon** | Off | Lets the planner charge purely to hold energy past the end of its horizon, valued at the measured replacement cost. Without it, every charge must pair with a discharge the planner can already see. Changes what the battery buys, so it is off unless you switch it on. |
| **Replacement cost window (days)** | 30 | Only shown with carry-forward on. Trailing window the cheapest all-in buy price of each day is taken from. Longer = steadier but slower to react to a season change; shorter = follows the market but noisier. Incomplete days are skipped. |
| **Replacement cost percentile** | 25 | Only shown with carry-forward on. Which percentile of those daily-cheapest prices becomes the replacement cost. Keep it low — set too high and charging is always attractive, so the battery ends up full and idle. Also capped at the window's median buy price. |
| **Learn the discount and night reserve from measured forecast error** | Off | Nightly fit over 21 days of stored forecasts vs. what happened; overwrites **Future value discount** and the **night reserve** above. Discount from forecast drift with lead time; reserve from the 80th percentile of measured 21:00–07:00 draw. Writes nothing until it has enough history. A learned value hitting its bound is reported under Tips & Checks. **Last learned** shows when it last ran. |

### Estimated home energy needs per month (kWh)

Twelve values, one per month — your household's expected monthly consumption. Used as the fallback
demand profile when live/weather-based consumption is unavailable, so the planner still sizes the
reserve sensibly.

### Statistics

| Setting | Default | What it does |
| --- | --- | --- |
| **Statistics from date (optional)** | empty (all data) | Cuts off everything before this date on the statistics pages. Leave empty to use all data. |

## User interface

### Navigation menu

| Setting | Default | What it does |
| --- | --- | --- |
| **Keep the menu expanded after clicking an item** | On | On: the menu stays as you left it (expanded menus keep their labels across pages). Off: every click folds it back to icons only. On a phone the expanded menu covers part of the page, so turn it off there if icons suit you. |

## Other tabs

These tabs manage data or expose tools rather than plain settings:

- **Investment Groups** — define groups (battery, solar, heat pump …) that the payback and
  return-on-investment figures are broken down by.
- **Investments** — the cost and, for the battery, the cycle life of each investment. Feeds the
  calculated cycle cost and the payback/ROI statistics.
- **Taxes** — the tax and tariff components that turn a raw EPEX price into the all-in buy/sell price
  the planner and statistics use.
- **Who's in control** — shows which controller is driving the batteries (SessyWeb, Charged, or the
  provider) and the handover state.
- **Tips & Checks** — diagnostics: configuration problems, stale data, and planner sanity checks. A
  badge on the tab flags errors (red) or warnings (orange).
- **SQL Console** — run SQL against the database. Statements separated by semicolons run in order in
  one transaction (a failure rolls everything back). Take a backup before anything destructive.
- **Container log** — the live application log in a scrollable window, with auto-scroll, a level
  filter, and copy/export. Keeps the most recent 1000 lines.
- **Credits** — acknowledgements.

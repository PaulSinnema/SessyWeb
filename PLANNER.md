# How the planner works

This document describes what SessyWeb's planner actually does, and which setting changes which part
of it. It is the level below [README.md](README.md): that one tells you how to run SessyWeb, this one
tells you why the plan looks the way it does.

Source: `SessyController/Services/Optimization/BatteryGreedyPlanner.cs` for the search,
`SessyController/Services/StrategyMilpService.cs` for what goes into it, and
`SessyController/Services/MilpServiceBase.cs` for the bookkeeping around it.

> [!NOTE]
> The class names say `Milp`. There is no MILP and no solver library — those names are left over from
> the original design. What runs is the deterministic greedy search described below.

---

## Table of contents

- [The short version](#the-short-version)
- [What the planner is given](#what-the-planner-is-given)
- [The search](#the-search)
  - [Pass 1 — baseline](#pass-1--baseline)
  - [Pass 2 — arbitrage](#pass-2--arbitrage)
  - [Pass 3 — classification](#pass-3--classification)
- [The four things that shape the answer](#the-four-things-that-shape-the-answer)
- [When the plan is rebuilt](#when-the-plan-is-rebuilt)
- [From plan to hardware](#from-plan-to-hardware)
- [Settings reference](#settings-reference)
- [appsettings.json reference](#appsettingsjson-reference)
- [What the planner measures for itself](#what-the-planner-measures-for-itself)
- [Reading a plan that looks wrong](#reading-a-plan-that-looks-wrong)

---

## The short version

Every quarter-hour is a slot. For each slot the planner knows the buy price, the sell price and the
net load (household consumption minus solar). It starts from the SOC the batteries actually report,
then does two passes: first cover the house from solar and battery where that is free, then
repeatedly take the single most profitable 0.2 kWh trade it can still fit, until nothing profitable
is left.

That is the whole idea. Everything else is a limit on it — how much power fits in a quarter, how much
survives the round trip, how low the battery may go, and how far ahead it can see.

---

## What the planner is given

Per quarter (`PricePoint`):

| | Meaning |
|---|---|
| `BuyEurPerKWh` / `SellEurPerKWh` | All-in prices — market price plus energy tax, surcharges and VAT from the **Taxes** page. The planner never sees a raw market price |
| `NetLoadWh` | Household load minus solar. Positive = the house needs the grid, negative = solar surplus |
| `MaxChargeKW` / `MaxDischargeKW` | Per-quarter power ceilings, when a temperature throttle applies |
| `ReserveOnly` | The price is predicted, not published: usable for reserving energy, not for trading |
| `TemperatureC` / `Temperature48hC` | Feed the charge taper — the second is the 48-hour mean, for heat build-up |

Once for the whole run (`BatterySpec`): capacity, starting SOC, nameplate charge and discharge power,
one-way efficiencies, and four measured models — the charge taper, the charge capability floor, the
discharge capability and the efficiency curve. [What the planner measures for
itself](#what-the-planner-measures-for-itself) covers those.

Per quarter, a floor and a ceiling on the SOC (`SocBound`). The ceiling is capacity. The floor is the
night reserve, described under [the four things](#the-four-things-that-shape-the-answer).

**The horizon is whatever the prices reach.** Day-ahead prices exist to the end of tomorrow and no
further, so the plan is 24 to 48 hours long depending on the time of day. Until tomorrow's prices are
published around 13:00 they are filled in from a 60-day historical average per quarter.

---

## The search

### Pass 1 — baseline

Walk the quarters in order, carrying the SOC. Where there is a solar surplus, store what fits; where
the house has a deficit, cover it from the battery, never below the reserve. Whatever is left over is
exported or imported.

This pass makes no price decisions at all. It establishes what happens if the battery only ever
serves the house — and, importantly, it leaves behind per quarter how much grid import and export
remains, which is exactly what the next pass prices.

### Pass 2 — arbitrage

Repeat until nothing profitable remains: score every possible trade, take the best one, allocate
0.2 kWh to it, update the SOC path, and score again.

Two prices drive every trade, and which one applies depends on what the baseline left behind:

**What a delivered kWh at quarter `j` is worth**
- the **buy** price, if there is still grid import left at `j` — the kWh avoids buying it
- the **sell** price, if there is not — the kWh is exported

**What a charged kWh at quarter `i` costs**
- the **sell** price, if there is still export left at `i` — you are giving up that revenue
- the **buy** price, if there is not — you are importing it

Then, per kWh delivered:

```
profit = value[j] − cost[i] / (chargeEff × dischargeEff) − cycleCost
```

The division is the round trip: delivering one kWh means putting `1 / (chargeEff × dischargeEff)`
into the battery. `cycleCost` is battery wear, in €/kWh discharged.

Four candidate shapes are scored every iteration:

| | Shape | Exists because |
|---|---|---|
| **A** | Discharge energy the battery **already holds** | No charge quarter is needed. What that energy originally cost is sunk and deliberately ignored; instead the **replacement cost** is a floor — see below |
| **B** | Charge at `i`, discharge at `j`, with `i < j` | The ordinary buy-low-sell-high |
| **C** | Charge at `i` and **keep** it past the end of the horizon | Off by default. Without it the planner can never buy for a payoff it cannot see — which is exactly the case when prices go negative |
| **D** | Discharge at `j`, **buy it back** at `k > j` | Candidate A may only sell if the SOC path stays above the reserve all the way to the end. Pairing a sale with its repurchase lifts the path back up after `k`, which frees energy that A cannot touch |

Feasibility is checked before allocation: the charge must fit in that quarter's remaining charge
power, the discharge in its remaining discharge power, and the SOC path must stay inside its bounds on
every quarter in between — above the reserve where it dips, below capacity where it rises.

**Candidate A's floor.** A kWh still in the battery when the horizon ends is worth nothing to the
objective, so without a floor the planner would dump stock at any price above the cycle cost. The
replacement cost — what putting that kWh back will really cost, measured from your own price history
— is therefore subtracted as a reservation price. Note the direction: it is not a cost to recover
(that would be a sunk-cost error), it is what you would have to pay to undo the sale.

### Pass 3 — classification

The SOC path is rebuilt from the allocations and each quarter gets a mode:

- grid-fed charging → **Charge**
- battery energy leaving the house → **Discharge**
- everything else, so storing solar or covering the house → **ZeroNetHome**, where the battery
  regulates itself and no setpoint is sent

---

## The four things that shape the answer

Everything above is mechanics. Four numbers decide what actually comes out.

**1. Cycle cost** — the profit threshold. A trade must beat the round-trip loss *plus* this. It is
not a setting you type: it is derived from your battery investments as
`net purchase price / (capacity × expected cycles)`, so entering your batteries on the
**Investments** page is what makes it real. Without that data it falls back to €0.05/kWh. The
strategy multiplies it: ×1 for profit maximization, ×1.5 for balanced, ×2 for battery saving.

**2. Night reserve** — the floor the plan may not sell through. For each quarter the planner scans
forward to the next solar window and adds up the household load it finds, then applies the safety
factor and caps it at `NightReserveCapPct` of capacity. Two subtleties:

- The scan stops at the **second** solar window, not the first. Reserving across a whole sunny day
  meant that at sunrise the battery was still holding energy for the *next* evening, and so could not
  sell into the peak it was sitting in.
- Where the scan runs off the end of the horizon rather than reaching a sunrise, the night behind it
  is not in the data at all. The learned whole-night need is used instead. This is why the last
  evening in the plan does not sell the battery empty, and it is the single most common "why is it
  keeping charge?" question.

**3. Replacement cost** — the floor under selling stock, and the value of carrying it forward.
Measured as a low percentile (default P25) of the daily cheapest all-in buy price over a trailing
window (default 30 days), capped at the median buy price of that same window. Deliberately low: set
it high and charging always looks attractive while selling never does, and the battery ends up full
and idle. Until there is enough price history the FIFO cost basis of what is actually in the battery
stands in.

**4. Future-value discount** — the tie-breaker between now and later. In the profit comparison only,
a quarter `h` hours away counts for `1 / (1 + rate × h)` of its real price. At the default 0.003 per
hour a peak 24 hours out must be roughly 7% better to beat one available tonight. The reported
objective and the executed plan use full prices; this only shapes which of several similar quarters
the search reaches for, so a single far-off peak cannot claim the whole battery on a forecast that
far out.

---

## When the plan is rebuilt

The cycle runs every 60 seconds, but a rebuild is not free and the plan is not thrown away lightly.
In priority order:

| Trigger | Forced? |
|---|---|
| No plan exists | yes |
| A setting changed that affects planning | yes |
| First run, or just restored from the database | yes |
| New day-ahead prices arrived | yes |
| SOC drifted more than 20% from the plan — **only while SessyWeb is driving** | yes |
| Once per quarter, speculatively | no |

A **forced** rebuild is always kept. A **speculative** one has to earn its place: it is compared on
€ per quarter, not on the raw total, because the horizon shrinks as the day passes and a later plan's
total is smaller even when it is better. If it does not beat the plan in place it is rolled back
completely — the plan, the SOC path and the anchor it is measured against.

The SOC-deviation trigger is off while Charged is in control on purpose: under Charged the SOC walks
away from our plan by design. That is someone else steering, not a deviation to correct.

---

## From plan to hardware

The plan says what should happen. Four guards decide what is actually sent, each quarter:

- **`GUARD_CHARGE_NO_ROOM`** — no usable room left in the battery, fall back to ZeroNetHome.
- **`GUARD_CHARGE_TARGET_REACHED`** — the charging session has already reached the SOC the plan
  wanted at the end of this run of charging quarters. Charging faster than planned is free within one
  price block, but the tail must not buy extra.
- **`GUARD_DISCHARGE_NO_ENERGY`** — nothing usable above the reserve.
- **Solar floor on the setpoint** — the charge setpoint is at least the current solar surplus, so a
  modest planned charge never exports energy the battery could have taken.

One thing here is easy to get backwards. The planner plans **two** power numbers: what it expects to
*arrive* (taper included, which drives the SOC path) and what the batteries are *asked* for. Where
the taper was the binding cap, the request is the untapered limit. The batteries throttle themselves,
so asking for the already-reduced number only ever made the throttle measurement measure its own
request — a loop in which every ratio reproduced itself and could never recover.

---

## Settings reference

On the **Settings** page, stored in the database, live without a restart.

### Directly on the planner

| Setting | Default | What it does |
|---|---|---|
| **Optimization strategy** | Balanced | Picks the objective. *Profit maximization* uses the cycle cost as derived; *Balanced* ×1.5; *Battery saving* ×2; *Self consumption* forbids export entirely, so the battery only stores solar and covers the house |
| **Night reserve cap** | 33% | Ceiling on the reserve, as a percentage of capacity. Also the whole-night estimate used where the horizon cuts a night short. Shown on **Statistics → Current Plan** |
| **Reserve safety factor** | 1.10 | Margin on the calculated reserve. Raise it if the battery regularly runs out overnight |
| **Future value discount** | 0.3 %/hour | The time preference described above. The UI shows a percentage; the stored value is 0.003. 0 disables it |
| **Planning horizon hours** | 0 (no limit) | Ignore quarters beyond N hours. Rarely needed — the horizon is already bounded by the published prices |
| **Predicted price mode** | Off | *Off*: predicted quarters extend the horizon for reserve purposes but are never traded. *SoftMargin*: traded, with the risk margin below applied against you. *Full*: trusted as published prices. Raise the future-value discount if you move off *Off* — the two belong together |
| **Predicted price risk margin** | €0.05/kWh | In *SoftMargin*, added to predicted buy prices and taken off predicted sell prices |
| **Carry forward enabled** | off | Allows Candidate C. Changes what the planner *buys*, so it is deliberate. On measured data it is close to a no-op in an ordinary summer, and earns its keep at negative prices |
| **Replacement cost window days** | 30 | Trailing window for the replacement cost |
| **Replacement cost percentile** | 25 | Which percentile of the daily cheapest price becomes the replacement cost |
| **Self learning enabled** | off | A nightly fit overwrites the future-value discount and the night reserve from your own measurements. Those two fields become read-only while it is on |
| **Monthly household consumption** | — | Twelve monthly figures in kWh, the base for the consumption forecast. Wrong here means wrong everywhere downstream |
| **Latitude / longitude** | — | Sunrise and sunset, which gate every "is it daylight" decision |
| **Annual solar production** | — | Scales the solar forecast |

### Fallbacks, used only until enough has been measured

| Setting | Default | Replaced by |
|---|---|---|
| **Throttle fallback** | 80% | The measured throttle ratio per temperature bucket |
| **Round-trip efficiency fallback** | 90% | The measured efficiency curve. One-way efficiency is `sqrt(roundTrip)` |

### Control, not planning

**Charged in control** hands the batteries to Sessy's own scheduler; SessyWeb keeps planning and
recording but sends nothing. **Manual override** is not an off switch — it means SessyWeb drives the
hours you picked by hand instead of the plan. Neither changes how the plan is computed.

### Elsewhere in the UI

The **Taxes** page sets energy tax, surcharges, VAT and netting. The planner works exclusively on
all-in prices, so an empty Taxes page makes every number meaningless. The **Investments** page is what
gives the cycle cost a real value.

---

## appsettings.json reference

Infrastructure, read at startup and mostly re-read live. It reaches the planner indirectly but
decisively.

| Key | Effect on planning |
|---|---|
| `Sessy:Batteries:Batteries:*:Capacity` | Summed into total capacity — the scale of the whole SOC path |
| `Sessy:Batteries:Batteries:*:MaxCharge` / `MaxDischarge` | Summed into nameplate power. This is the ceiling; the measured taper, floor and capability work below it. **Per battery, in Watts — do not enter system totals** |
| `Sessy:Meters:Endpoints:*` | The P1 meter. No meter means no measured consumption, so no forecast worth having |
| `PowerSystems:Endpoints:*:SolarPanels:*` | Panel count, tilt, orientation, peak power, area, efficiency and the highest daily production ever seen. These feed the solar forecast, which is half of `NetLoadWh`. Needed whether you read an inverter over Modbus or measure through the Sessy |
| `PowerSystems:Endpoints:*:InverterMaxCapacity` | Ceiling on the measured production, and the throttle target during curtailment |
| `WeerOnline:*` | Radiation drives the solar forecast; temperature feeds the charge taper, including the 48-hour mean for heat build-up |
| `ENTSO-E:*` | Fallback price source when the batteries cannot be reached. Without it a battery outage means no prices and therefore no plan |

The timezone is **not** here — it is in the database, so there is no second place it can disagree with.
Quarter alignment is assumed throughout the planner.

---

## What the planner measures for itself

Five models are fitted from your own history. All of them fall back to something safe while there is
too little data, and all of them are visible on **Settings → Tips & Checks**.

| Model | What it captures | Why it is measured, not configured |
|---|---|---|
| **Charge taper** | Charge power falling off as the battery fills (CC/CV), plus temperature and heat build-up | Datasheet power is reached only at low SOC. Assuming nameplate plans a charging window that does not fit |
| **Charge capability floor** | Per 5% SOC bin, the P90 of charge power actually accepted, ×0.9 | The taper is fitted on a *ratio* and can only use quarters that recorded an untapered request — a narrow, one-sided slice. A high measurement *proves* the bank can do it; a low one proves nothing, since it may just be slow solar charging. So the floor is a percentile of the top, not a fit |
| **Discharge capability** | A plateau with a knee at low SOC | Low cell voltage buys less power at the same current limit. Measured against SOC; temperature explains nothing here (R² 0.012) |
| **Efficiency curve** | Efficiency rising with power — measured 0.80 below 1 kW against 0.92 at 4–5 kW | Much of the conversion loss is fixed overhead. With a constant efficiency, spreading energy over more quarters looks free, and the planner has no reason to concentrate it |
| **Replacement cost** | What a kWh will cost to put back | See [the four things](#the-four-things-that-shape-the-answer) |

With **self learning** on, a nightly pass additionally fits the future-value discount from measured
forecast error per lead time, and the night reserve from the P80 of what the house actually drew
between 21:00 and 07:00.

---

## Reading a plan that looks wrong

**The battery does not reach 100%.** Usually correct. Filling costs money and only pays if the energy
sells higher later. Judge on **Financial results**, not on the SOC.

**It sells into the evening and the SOC ends low on the last planned day.** Correct, and the floor is
the night reserve for the night beyond the horizon — visible on **Statistics → Current Plan**. If it
ended at nearly zero you are running a version before v1.0.103.

**It keeps charge through a high price.** One of three reasons, and they are distinguishable: the
sale was not profitable against the floor (replacement cost plus cycle cost), the quarter had no
discharge power left, or the SOC path could not go lower without breaking the reserve. The planner can
report which, per quarter — `BatteryGreedyPlanner.Solve` takes an optional trace callback that prints
exactly these three terms.

**Setpoint requested differs from setpoint.** Not a fault. The Sessy hardware tapers on its own
(CC/CV, SOC-dependent) and reports no reason. `SetpointRequested` is what we asked; `PowerSetpoint` is
what the device chose.

**Nothing in the log.** The production log level is deliberately `Warning`, and every model fit logs
at `Information`. Do not raise it — check the database instead. `PlannedQuarters` holds the plan per
quarter including the price, the net load, the reserve floor and the SOC the plan expected, which is
stronger evidence than any log line: it is what was actually planned.

### Replaying a plan exactly

Set the environment variable `SESSY_RECORD_SOLVE_INPUTS=1` and SessyWeb writes every solve's complete
input — price points, battery spec, options and SOC bounds — as JSON. That file replays through the
real planner in the unit tests, which is the only way to reason about a suspect plan without guessing
at reconstructed inputs. Reconstruction has produced wrong conclusions here more than once; the
recorder exists because of it.

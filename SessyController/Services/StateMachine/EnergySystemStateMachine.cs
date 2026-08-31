using Microsoft.Extensions.Logging;
using SessyCommon.Enums;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services.StateMachine
{
    /// <summary>
    /// The single source of all decisions about battery mode and inverter output.
    ///
    /// All transition logic lives here — nowhere else.
    /// BatteriesService calls Evaluate() and executes the result.
    /// InverterCurtailmentService reads CurrentAction to know what to do.
    ///
    /// Priority order (highest first):
    ///   1. Negative selling price → curtailment overrides MILP plan
    ///   2. MILP plan (Charging / Discharging / ZeroNetHome / Disabled)
    ///
    /// Curtailment modes and inverter setpoints:
    ///   ZERO_EXPORT  — price negative, battery charging.
    ///                  Battery keeps charging. Inverter: P1 throttle.
    ///   THROTTLE     — price negative, battery full.
    ///                  Battery disabled. Inverter: P1 throttle.
    ///   SHUTDOWN     — price negative, battery not full and not charging.
    ///                  Battery: forced charge at MaxChargeSetpointW. Inverter: 0W.
    ///                  At negative prices grid electricity is cheaper than free solar —
    ///                  shut the inverter down entirely and charge from the grid.
    ///
    /// InverterSetpointW semantics:
    ///   double.MaxValue = full output OR P1-controlled (CurtailmentMode determines which)
    ///   0.0             = hard shutdown
    /// </summary>
    public class EnergySystemStateMachine
    {
        private readonly ILogger<EnergySystemStateMachine> _logger;

        /// <summary>
        /// The most recently evaluated action.
        /// InverterCurtailmentService reads this every 5 seconds.
        /// Updated by every call to Evaluate().
        /// </summary>
        public EnergySystemAction CurrentAction { get; private set; } = new EnergySystemAction();

        /// <summary>
        /// How long a mode has to hold before an opposite change is accepted.
        ///
        /// Everything feeding this class decides on a bare comparison: the runtime guards test a
        /// live SOC against a fixed number of Wh, the idle branch tests the sign of a NetLoad that
        /// is recomputed every cycle, and the curtailment branch tests a battery power that its own
        /// previous decision caused. None of them has hysteresis. Charging, Discharging and
        /// ZeroNetHome now all run on NOM (the P1 grid target sets the power), so a value flickering
        /// around any of those thresholds churns the grid target every cycle, and any flicker across
        /// the Disabled boundary also rewrites the battery's power strategy — the instability an
        /// external installation reported.
        /// </summary>
        public static readonly TimeSpan MinimumModeDwell = TimeSpan.FromSeconds(120);

        /// <summary>When the mode currently in CurrentAction was accepted.</summary>
        private DateTime _modeSince = DateTime.MinValue;

        /// <summary>The mode last held back, so the suppression is logged once and not per cycle.</summary>
        private Modes _suppressedMode = Modes.Unknown;

        public EnergySystemStateMachine(ILogger<EnergySystemStateMachine> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Evaluates the current system state and returns the action to execute.
        /// Stores the result in CurrentAction for InverterCurtailmentService.
        /// </summary>
        public EnergySystemAction Evaluate(EnergySystemInput input)
        {
            var action = input.SellingPriceIsNegative
                ? EvaluateCurtailment(input)
                : EvaluatePlan(input);

            action = ApplyModeDwell(action, input.Now);

            // Only log when something changes.
            if (action.Reason != CurrentAction.Reason ||
                action.BatteryMode != CurrentAction.BatteryMode ||
                action.CurtailmentMode != CurrentAction.CurtailmentMode)
            {
                _logger.LogInformation(
                    $"EnergyStateMachine: [{action.CurtailmentMode}] " +
                    $"Battery={action.BatteryMode} ({action.BatterySetpointW:F0}W) " +
                    $"Override={action.IsOverride} — {action.Reason}");
            }

            CurrentAction = action;
            return action;
        }

        // ── Mode dwell ────────────────────────────────────────────────────────

        /// <summary>
        /// How active a mode is. ZeroNetHome and Disabled both leave the battery alone, Charging
        /// and Discharging both command power, so they rank equal within each pair.
        /// </summary>
        private static int ActivityRank(Modes mode)
            => mode == Modes.Charging || mode == Modes.Discharging ? 1 : 0;

        /// <summary>
        /// Whether a mode change may be executed now.
        ///
        /// Stopping is always allowed — holding an active mode because a timer has not expired is
        /// the one direction that could keep charging a full battery, so it is never delayed.
        /// Starting again, and swapping between two equally active modes, waits out the dwell.
        /// That asymmetry is what breaks the limit cycle without ever holding a dangerous state:
        /// the guard fires immediately, only the re-arm is rate limited.
        /// </summary>
        internal static bool MayChangeMode(Modes current, Modes candidate, DateTime modeSince, DateTime now, TimeSpan dwell)
        {
            if (candidate == current) return true;
            if (current == Modes.Unknown) return true;
            if (ActivityRank(candidate) < ActivityRank(current)) return true;

            return now - modeSince >= dwell;
        }

        /// <summary>
        /// Keeps the previous action when the new one would change mode too soon. Returns the
        /// action unchanged when the snapshot carries no clock, so a caller that never sets
        /// EnergySystemInput.Now keeps the old behaviour exactly.
        /// </summary>
        private EnergySystemAction ApplyModeDwell(EnergySystemAction action, DateTime now)
        {
            if (now == DateTime.MinValue) return action;

            var current = CurrentAction.BatteryMode;

            if (MayChangeMode(current, action.BatteryMode, _modeSince, now, MinimumModeDwell))
            {
                if (action.BatteryMode != current)
                    _modeSince = now;

                _suppressedMode = Modes.Unknown;
                return action;
            }

            // One line per suppressed transition, not per cycle — this runs every heartbeat.
            if (_suppressedMode != action.BatteryMode)
            {
                _suppressedMode = action.BatteryMode;

                _logger.LogWarning(
                    $"EnergyStateMachine: {current} → {action.BatteryMode} held back for " +
                    $"{(MinimumModeDwell - (now - _modeSince)).TotalSeconds:F0}s — {action.Reason}");
            }

            return CurrentAction;
        }

        // ── Curtailment branch ────────────────────────────────────────────────

        private EnergySystemAction EvaluateCurtailment(EnergySystemInput input)
        {
            // Nothing here can be executed without an inverter that accepts a setpoint. Not merely a
            // missed throttle: FORCE_CHARGE below draws maximum power from the grid precisely because
            // it assumes the inverter has been shut down, so pretending would be worse than the
            // negative price. Same fallback the offline branch already uses.
            if (!input.CurtailmentIsPossible)
            {
                return new EnergySystemAction
                {
                    BatteryMode = input.PlannedMode,
                    BatterySetpointW = input.PlannedSetpointW,
                    CurtailmentMode = CurtailmentMode.None,
                    Reason = "Selling price negative but the solar source cannot be throttled — following the MILP plan",
                    IsOverride = false
                };
            }

            // ZERO_EXPORT: battery is actually charging (includes NZH autonomous charging).
            // Keep the battery in its planned mode (Charging or NZH).
            // Inverter is P1-throttled — InverterCurtailmentService handles the control loop.
            if (input.BatteryIsActuallyCharging)
            {
                return new EnergySystemAction
                {
                    BatteryMode = input.PlannedMode == Modes.Charging
                                            ? Modes.Charging
                                            : Modes.ZeroNetHome,
                    BatterySetpointW = input.PlannedMode == Modes.Charging
                                            ? input.PlannedSetpointW
                                            : 0.0,
                    CurtailmentMode = CurtailmentMode.ZeroExport,
                    Reason = "Selling price negative + battery charging → ZERO_EXPORT",
                    IsOverride = true
                };
            }

            // THROTTLE: battery is full — cannot absorb more solar.
            // Disable battery. Inverter is P1-throttled to consumption only.
            if (input.BatteryIsFull)
            {
                return new EnergySystemAction
                {
                    BatteryMode = Modes.Disabled,
                    BatterySetpointW = 0.0,
                    CurtailmentMode = CurtailmentMode.Throttle,
                    Reason = "Selling price negative + battery full → THROTTLE",
                    IsOverride = true
                };
            }

            // FORCE_CHARGE: battery not full and not charging during negative price.
            // Charge at maximum power from the grid — at negative prices you are paid
            // to consume, so grid electricity is cheaper than free solar.
            // Inverter is shut down entirely to maximise grid consumption.
            if (!input.InverterIsAvailable)
            {
                _logger.LogWarning("EnergyStateMachine: FORCE_CHARGE requested but inverter offline — falling back to MILP plan.");
                return new EnergySystemAction
                {
                    BatteryMode = input.PlannedMode,
                    BatterySetpointW = input.PlannedSetpointW,
                    CurtailmentMode = CurtailmentMode.None,
                    Reason = "Selling price negative but inverter offline — falling back to MILP plan",
                    IsOverride = false
                };
            }

            return new EnergySystemAction
            {
                BatteryMode = Modes.Charging,
                BatterySetpointW = input.MaxChargeSetpointW,
                InverterSetpointW = 0.0,
                CurtailmentMode = CurtailmentMode.Shutdown,
                Reason = $"Selling price negative + battery not full/charging → FORCE_CHARGE at {input.MaxChargeSetpointW:F0}W, inverter 0W",
                IsOverride = true
            };
        }

        // ── Normal plan branch ────────────────────────────────────────────────

        private EnergySystemAction EvaluatePlan(EnergySystemInput input)
        {
            return input.PlannedMode switch
            {
                Modes.Charging => new EnergySystemAction
                {
                    BatteryMode = Modes.Charging,
                    BatterySetpointW = input.PlannedSetpointW,
                    CurtailmentMode = CurtailmentMode.None,
                    Reason = $"MILP: Charging at {input.PlannedSetpointW:F0}W"
                },

                Modes.Discharging => new EnergySystemAction
                {
                    BatteryMode = Modes.Discharging,
                    BatterySetpointW = input.PlannedSetpointW,
                    CurtailmentMode = CurtailmentMode.None,
                    Reason = $"MILP: Discharging at {input.PlannedSetpointW:F0}W"
                },

                Modes.ZeroNetHome => new EnergySystemAction
                {
                    BatteryMode = Modes.ZeroNetHome,
                    CurtailmentMode = CurtailmentMode.None,
                    Reason = "MILP: Zero Net Home"
                },

                _ => new EnergySystemAction
                {
                    BatteryMode = Modes.Disabled,
                    CurtailmentMode = CurtailmentMode.None,
                    Reason = "MILP: Disabled"
                }
            };
        }
    }
}
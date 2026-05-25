using Microsoft.Extensions.Logging;
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

        // ── Curtailment branch ────────────────────────────────────────────────

        private EnergySystemAction EvaluateCurtailment(EnergySystemInput input)
        {
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
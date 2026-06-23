using SessyCommon.Enums;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services.StateMachine
{
    /// <summary>
    /// The complete output of EnergySystemStateMachine.Evaluate().
    /// Describes exactly what the battery system and solar inverter must do this cycle.
    ///
    /// InverterSetpointW semantics:
    ///   double.MaxValue = full output (default — no curtailment, or P1-throttle mode)
    ///   0.0             = hard shutdown (SHUTDOWN mode only)
    ///
    /// When CurtailmentMode is ZeroExport or Throttle, InverterSetpointW is MaxValue
    /// and InverterCurtailmentService runs its P1 control loop to find the right setpoint.
    /// When CurtailmentMode is Shutdown, InverterSetpointW is 0.0.
    /// </summary>
    public class EnergySystemAction
    {
        // ── Battery ───────────────────────────────────────────────────────────

        /// <summary>Mode to set on the Sessy batteries.</summary>
        public Modes BatteryMode { get; init; } = Modes.ZeroNetHome;

        /// <summary>Power setpoint in Watts for Charging or Discharging. 0 for NZH/Disabled.</summary>
        public double BatterySetpointW { get; init; } = 0.0;

        // ── Inverter ──────────────────────────────────────────────────────────

        /// <summary>
        /// Target inverter output in Watts.
        /// double.MaxValue = full output or P1-controlled (see CurtailmentMode).
        /// 0.0             = hard shutdown (SHUTDOWN curtailment only).
        /// </summary>
        public double InverterSetpointW { get; init; } = double.MaxValue;

        // ── Curtailment mode ──────────────────────────────────────────────────

        /// <summary>
        /// Curtailment mode for InverterCurtailmentService and UI display.
        /// None       → inverter at full output, no P1 control.
        /// ZeroExport → InverterCurtailmentService runs P1 throttle (consumption + charging).
        /// Throttle   → InverterCurtailmentService runs P1 throttle (consumption only).
        /// Shutdown   → InverterCurtailmentService sets inverter to 0W.
        /// </summary>
        public CurtailmentMode CurtailmentMode { get; init; } = CurtailmentMode.None;

        // ── Diagnostics ───────────────────────────────────────────────────────

        /// <summary>Human-readable explanation of why this action was chosen.</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>True when the action overrides the MILP plan (e.g. curtailment).</summary>
        public bool IsOverride { get; init; } = false;
    }
}
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SessyCommon.Extensions;

namespace SessyData.Model
{
    /// <summary>
    /// Records the actual system state at the start of each quarter.
    /// Written once per quarter by BatteriesService after state machine evaluation.
    ///
    /// JOIN with PlannedQuarter on Time for plan vs actual comparison.
    /// </summary>
    public class ActualQuarter : IUpdatable<ActualQuarter>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Quarter start time (local timezone). Unique per quarter.</summary>
        public DateTime Time { get; set; }

        // ── Actual battery state ───────────────────────────────────────────────

        /// <summary>Actual battery strategy as reported by the Sessy hardware.</summary>
        public string ActualMode { get; set; } = string.Empty;

        /// <summary>Actual battery power (W). Negative = charging, positive = discharging.</summary>
        public double ActualPowerW { get; set; }

        /// <summary>Actual state of charge at the start of this quarter (Wh).</summary>
        public double ActualSocWh { get; set; }

        // ── State machine decision ─────────────────────────────────────────────

        /// <summary>Curtailment mode active at the start of this quarter (None/ZeroExport/Throttle/Shutdown).</summary>
        public string CurtailmentMode { get; set; } = string.Empty;

        /// <summary>State machine reason for the action taken this quarter.</summary>
        public string StateMachineReason { get; set; } = string.Empty;

        public void Update(ActualQuarter updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SessyCommon.Extensions;

namespace SessyData.Model
{
    /// <summary>
    /// Persists one quarter of the MILP plan for tombstoning across restarts.
    /// The plan is saved after each successful solve and restored on startup
    /// if it is not older than <see cref="MaxPlanAgeHours"/> hours.
    /// </summary>
    public class PlannedAction : IUpdatable<PlannedAction>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Unique identifier for this plan solve — all quarters of one solve share the same PlanId.</summary>
        public Guid PlanId { get; set; }

        /// <summary>Quarter start time (UTC).</summary>
        public DateTime Time { get; set; }

        /// <summary>Planned mode: Charging, Discharging, ZeroNetHome, Disabled.</summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>Planned power setpoint in Watts.</summary>
        public double PowerW { get; set; }

        /// <summary>When this plan was written to the database.</summary>
        public DateTime SavedAt { get; set; }

        /// <summary>MILP objective value (combined expected profit in EUR) at time of solve.</summary>
        public double ObjectiveEur { get; set; }

        /// <summary>Price signature at time of solve — used to detect price changes after restore.</summary>
        public long PriceSignature { get; set; }

        /// <summary>Why this plan was generated.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Plans older than this are not restored on startup.</summary>
        public const int MaxPlanAgeHours = 2;

        /// <summary>Plans older than this are purged from the database.</summary>
        public const int MaxPlanRetentionDays = 7;

        public void Update(PlannedAction updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}
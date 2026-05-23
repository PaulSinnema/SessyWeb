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

        /// <summary>Quarter start time (UTC).</summary>
        public DateTime Time { get; set; }

        /// <summary>Planned mode: Charging, Discharging, ZeroNetHome, Disabled.</summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>Planned power setpoint in Watts.</summary>
        public double PowerW { get; set; }

        /// <summary>When this plan was last written to the database.</summary>
        public DateTime SavedAt { get; set; }

        /// <summary>Price signature at time of solve — used to detect price changes after restore.</summary>
        public int PriceSignature { get; set; }

        /// <summary>Plans older than this are not restored on startup.</summary>
        public const int MaxPlanAgeHours = 2;

        public void Update(PlannedAction updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}
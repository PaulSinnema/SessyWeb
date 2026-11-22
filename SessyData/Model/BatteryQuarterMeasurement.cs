using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SessyCommon;

namespace SessyData.Model
{
    /// <summary>
    /// Battery telemetry per quarter-hour for ONE battery, written next to the bank total in
    /// QuarterlyMeasurement and with the same snapshot semantics (the last reading in the quarter wins).
    /// </summary>
    [Index(nameof(Time), nameof(BatteryId), IsUnique = true)]
    public class BatteryQuarterMeasurement : IUpdatable<BatteryQuarterMeasurement>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Quarter-hour start timestamp (local time).</summary>
        public DateTime Time { get; set; }

        /// <summary>Battery key from appsettings (Sessy:Batteries).</summary>
        public string BatteryId { get; set; } = string.Empty;

        /// <summary>Configured display name at the time of the reading.</summary>
        public string? Name { get; set; }

        /// <summary>Watts; negative = charging, positive = discharging.</summary>
        public double PowerWatts { get; set; }

        /// <summary>State of charge in Wh.</summary>
        public double StateOfChargeWh { get; set; }

        /// <summary>Configured capacity in Wh.</summary>
        public double CapacityWh { get; set; }

        [NotMapped]
        public double ChargedKWh => Units.ChargedKWh(PowerWatts);

        [NotMapped]
        public double DischargedKWh => Units.DischargedKWh(PowerWatts);

        public void Update(BatteryQuarterMeasurement updateInfo)
        {
            Name = updateInfo.Name;
            PowerWatts = updateInfo.PowerWatts;
            StateOfChargeWh = updateInfo.StateOfChargeWh;
            CapacityWh = updateInfo.CapacityWh;
        }
    }
}

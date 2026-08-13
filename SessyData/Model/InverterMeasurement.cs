using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Per-inverter solar production per quarter-hour, and the only measured source of it — the
    /// Solar power page, the statistics and the performance factor all read this table.
    ///
    /// One row per inverter per quarter, so a total means summing the rows for that quarter.
    /// QuarterlyMeasurement has no solar column; this comment used to say it held the total.
    /// </summary>
    [Index(nameof(Time))]
    [Index(nameof(InverterId))]
    public class InverterMeasurement : IUpdatable<InverterMeasurement>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Quarter-hour start timestamp (local time).</summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// Inverter identifier — matches the inverter Id from PowerSystems config.
        /// e.g. "1", "2".
        /// </summary>
        public string InverterId { get; set; } = string.Empty;

        /// <summary>
        /// Inverter provider/brand name for display purposes.
        /// e.g. "SolarEdge", "Enphase".
        /// </summary>
        public string ProviderName { get; set; } = string.Empty;

        /// <summary>Solar energy produced by this inverter this quarter in kWh.</summary>
        public double SolarProductionKWh { get; set; }

        public override string ToString() =>
            $"{Time:yyyy-MM-dd HH:mm} | Inverter={InverterId} ({ProviderName}) | {SolarProductionKWh:F4} kWh";

        public void Update(InverterMeasurement updateInfo)
        {
            ProviderName = updateInfo.ProviderName;
            SolarProductionKWh = updateInfo.SolarProductionKWh;
        }
    }
}
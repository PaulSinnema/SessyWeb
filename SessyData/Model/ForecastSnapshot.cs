using Microsoft.EntityFrameworkCore;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// The solar and consumption forecast for one quarter, as it looked a given number of hours
    /// before that quarter started.
    ///
    /// PlannedQuarter also carries a forecast, but it is upserted on every rebuild, so by the
    /// time the quarter is in the past it holds the forecast made moments before it — which is
    /// useless for measuring how wrong a forecast is a day out. Rows here are written once per
    /// (quarter, lead bucket) and never updated, so the history keeps the forecast at each
    /// distance intact.
    ///
    /// Consumed by PlannerLearningService to derive the future-value discount and the night
    /// reserve from measured forecast error instead of hand-picked numbers.
    /// </summary>
    [Index(nameof(Time))]
    public class ForecastSnapshot : IUpdatable<ForecastSnapshot>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Quarter being forecast (local timezone).</summary>
        public DateTime Time { get; set; }

        /// <summary>Whole hours between the moment of the forecast and <see cref="Time"/>.</summary>
        public int LeadHours { get; set; }

        /// <summary>Solar production forecast for this quarter (W).</summary>
        public double SolarForecastW { get; set; }

        /// <summary>Estimated consumption for this quarter (W).</summary>
        public double ConsumptionForecastW { get; set; }

        /// <summary>When this row was written.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Snapshots older than this are purged; the learner looks back three weeks.</summary>
        public const int MaxRetentionDays = 40;

        /// <summary>
        /// Lead times that are actually stored. A fixed ladder instead of every hour keeps the
        /// table small while still covering the whole 48-hour horizon at useful resolution.
        /// </summary>
        public static readonly int[] LeadBuckets = [0, 1, 2, 3, 4, 6, 8, 12, 16, 20, 24, 30, 36, 48];

        /// <summary>Largest ladder value at or below <paramref name="leadHours"/>.</summary>
        public static int BucketFor(double leadHours)
        {
            int bucket = LeadBuckets[0];
            foreach (int candidate in LeadBuckets)
                if (candidate <= leadHours) bucket = candidate;
            return bucket;
        }

        public void Update(ForecastSnapshot updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}

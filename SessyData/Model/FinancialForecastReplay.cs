using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// One planner replay of the last 12 full months at next year's tariffs (Statistics, Financial
    /// forecast). At most one row per week; the newest row is what the page shows.
    /// </summary>
    [Index(nameof(CalculatedAt))]
    public class FinancialForecastReplay
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        public DateTime CalculatedAt { get; set; }

        /// <summary>Days actually replayed (days with missing prices are skipped).</summary>
        public int Days { get; set; }

        public string Label { get; set; } = string.Empty;

        public DateTime From { get; set; }

        public DateTime To { get; set; }

        public bool Netting { get; set; }

        public double ImportKWh { get; set; }

        public double ExportKWh { get; set; }

        public double ImportCostEur { get; set; }

        public double ExportRevenueEur { get; set; }

        public double EnergyTaxEur { get; set; }

        public double FixedCostsEur { get; set; }

        public double TaxReductionEur { get; set; }
    }
}

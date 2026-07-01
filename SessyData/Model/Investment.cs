using Microsoft.EntityFrameworkCore;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Represents a financial investment in the energy system.
    /// Used to calculate the return on investment (ROI) and payback period.
    /// </summary>
    [Index(nameof(PurchaseDate))]
    public class Investment : IUpdatable<Investment>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Description of the investment (e.g. "12x JA Solar 400Wp", "3x Sessy 5.4kWh").
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Purchase date of the component.
        /// </summary>
        public DateTime PurchaseDate { get; set; }

        /// <summary>
        /// Total purchase amount in EUR (including VAT and installation).
        /// </summary>
        public double AmountEur { get; set; }

        /// <summary>
        /// Any subsidies or tax rebates received for this investment in EUR.
        /// </summary>
        public double SubsidyEur { get; set; }

        /// <summary>
        /// Net investment after subsidies.
        /// </summary>
        [NotMapped]
        public double NetAmountEur => AmountEur - SubsidyEur;

        /// <summary>
        /// Optional: expected lifetime in years (for depreciation calculation).
        /// </summary>
        public int ExpectedLifetimeYears { get; set; } = 25;

        /// <summary>
        /// Optional foreign key to InvestmentGroup.
        /// When set, this investment is combined with other group members
        /// for a single ROI calculation.
        /// </summary>
        public int? InvestmentGroupId { get; set; }

        /// <summary>
        /// Optional: manually estimated annual savings in EUR for components
        /// where automatic calculation is not possible (e.g. heat pump vs gas).
        /// When set, this overrides the automatic savings calculation.
        /// Leave at 0 to use automatic calculation.
        /// </summary>
        public double EstimatedAnnualSavingsEur { get; set; } = 0.0;

        /// <summary>
        /// Optional: description of how savings are calculated for this component.
        /// </summary>
        public string? SavingsDescription { get; set; }

        /// <summary>
        /// Optional notes.
        /// </summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Battery capacity of this investment in Wh. Only relevant for investments
        /// in a group with Category == Storage. Used to derive the cycle cost.
        /// </summary>
        public double CapacityWh { get; set; } = 0.0;

        /// <summary>
        /// Expected total number of full charge/discharge cycles over the battery's
        /// lifetime. Only relevant for Storage investments. Used to derive cycle cost:
        /// cost = NetAmountEur / (CapacityWh/1000 * ExpectedTotalCycles).
        /// </summary>
        public int ExpectedTotalCycles { get; set; } = 0;

        public void Update(Investment updateInfo)
        {
            this.Copy(updateInfo);
        }

        public override string ToString()
        {
            return $"Id: {Id}, Description: {Description}, " +
                   $"PurchaseDate: {PurchaseDate:dd-MM-yyyy}, Amount: {AmountEur:F2} EUR, " +
                   $"Subsidy: {SubsidyEur:F2} EUR, Net: {NetAmountEur:F2} EUR";
        }
    }
}
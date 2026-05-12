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
        /// Category of the investment (e.g. SolarPanels, Battery, HeatPump, Inverter, Installation).
        /// </summary>
        public string Category { get; set; } = string.Empty;

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
        /// Optional: manually estimated annual savings in EUR for components
        /// where automatic calculation is not possible (e.g. heat pump vs gas).
        /// When set, this overrides the automatic savings calculation.
        /// Leave at 0 to use automatic calculation.
        /// </summary>
        public double EstimatedAnnualSavingsEur { get; set; } = 0.0;

        /// <summary>
        /// Optional: description of how savings are calculated for this component.
        /// E.g. "950 m³ gas * €1.45/m³ + €185 standing charge"
        /// </summary>
        public string? SavingsDescription { get; set; }

        /// <summary>
        /// Optional notes.
        /// </summary>
        public string? Notes { get; set; }

        public void Update(Investment updateInfo)
        {
            this.Copy(updateInfo);
        }

        public override string ToString()
        {
            return $"Id: {Id}, Category: {Category}, Description: {Description}, " +
                   $"PurchaseDate: {PurchaseDate:dd-MM-yyyy}, Amount: {AmountEur:F2} EUR, " +
                   $"Subsidy: {SubsidyEur:F2} EUR, Net: {NetAmountEur:F2} EUR";
        }
    }
}
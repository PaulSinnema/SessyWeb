using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class Taxes : IUpdatable<Taxes>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        /// <summary>
        /// From when are these tariffs valid
        /// </summary>
        public DateTime? Time { get; set; }
        /// <summary>
        /// Energy taxes are inclusive ODE.
        /// </summary>
        public double EnergyTax { get; set; }
        public double ValueAddedTax { get; set; }
        /// <summary>
        /// What I pay the supplier for purchasing per kWh.
        /// </summary>
        public double PurchaseCompensation { get; set; }
        /// <summary>
        /// What I pay the supplier for delivery per kWh: positive = cost, negative = compensation (e.g. Zonneplan).
        /// </summary>
        public double ReturnDeliveryCompensation { get; set; }
        /// <summary>
        /// Return delivery costs as added to the selling price: a cost lowers it, a compensation raises it.
        /// </summary>
        [NotMapped]
        public double SignedReturnDeliveryCompensation => -ReturnDeliveryCompensation;

        /// <summary>1 + VAT as a fraction (e.g. 1.21).</summary>
        [NotMapped]
        public double VatFactor => 1.0 + ValueAddedTax / 100.0;

        /// <summary>The seed row written when the table is empty: the author's Frank Energie contract, excl. VAT.</summary>
        public static Taxes CreateDefault() => new Taxes
        {
            Time = new DateTime(2025, 1, 1, 0, 0, 0),
            EnergyTax = 0.10154,
            ValueAddedTax = 21.0,
            TaxReduction = 524.95,
            PurchaseCompensation = 0.01504,
            ReturnDeliveryCompensation = 0.0105,
            NetManagementCost = 376.6435,
            FixedTransportFee = 17.9945,
            CapacityTransportFee = 69.48 // Frank Energie fixed supply fee
        };
        /// <summary>
        /// Percentage some suppliers (e.g. Zonneplan) pay on top of the market price plus
        /// ReturnDeliveryCompensation for return delivery between sunrise and sunset, while the
        /// market price is positive. 0 = no bonus.
        /// </summary>
        public double SolarBonusPercentage { get; set; }
        /// <summary>
        /// Yearly tax reduction from the government.
        /// </summary>
        public double TaxReduction { get; set; }
        /// <summary>
        /// Yearly cost for net management (Capaciteitstarief).
        /// </summary>
        public double NetManagementCost { get; set; }
        /// <summary>
        /// Fixed fee for your connection (Vastrecht transport).
        /// </summary>
        public double FixedTransportFee { get; set; }
        /// <summary>
        /// Fee for transport depending on the capacity of the connection (Capaciteitstarief transport).
        /// </summary>
        public double CapacityTransportFee { get; set; }
        /// <summary>
        /// Is Netting (Saldering) enabled?
        /// </summary>
        public bool Netting { get; set; } = true;

        /// <summary>
        /// Energy tax on natural gas (Energiebelasting aardgas) in EUR per m³.
        /// In 2025: €0.3449/m³ (incl. ODE, excl. BTW).
        /// </summary>
        public double GasEnergyTaxEurPerM3 { get; set; } = 0.3449;

        /// <summary>
        /// Value added tax percentage on gas (BTW). In the Netherlands: 21%.
        /// Applied on top of market price + GasEnergyTaxEurPerM3.
        /// </summary>
        public double GasValueAddedTaxPct { get; set; } = 21.0;

        /// <summary>
        /// Supplier markup on gas in EUR per m³ (leveranciersopslag).
        /// This is the margin the energy supplier charges on top of the TTF market price.
        /// Typical value: €0.05 – €0.15/m³ depending on supplier and contract.
        /// </summary>
        public double GasSupplierMarkupEurPerM3 { get; set; } = 0.0;

        public override string ToString()
        {
            return $"Time: {Time}, Energy tax: {EnergyTax}, Netting: {Netting}, Value added tax: {ValueAddedTax}, PurchaseCompensation: {PurchaseCompensation}, ReturnDeliveryCompensation: {ReturnDeliveryCompensation}, SolarBonusPercentage: {SolarBonusPercentage}, TaxReduction: {TaxReduction}, NetManagementCost: {NetManagementCost}, FixedTransportFee: {FixedTransportFee}, CapacityTransportFee: {CapacityTransportFee}";
        }

        public void Update(Taxes updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}
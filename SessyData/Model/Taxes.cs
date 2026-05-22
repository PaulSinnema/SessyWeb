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
        /// What I pay the supplier for delivery per kWh.
        /// </summary>
        public double ReturnDeliveryCompensation { get; set; }
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
            return $"Time: {Time}, Energy tax: {EnergyTax}, Netting: {Netting}, Value added tax: {ValueAddedTax}, PurchaseCompensation: {PurchaseCompensation}, ReturnDeliveryCompensation: {ReturnDeliveryCompensation}, TaxReduction: {TaxReduction}, NetManagementCost: {NetManagementCost}, FixedTransportFee: {FixedTransportFee}, CapacityTransportFee: {CapacityTransportFee}";
        }

        public void Update(Taxes updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}
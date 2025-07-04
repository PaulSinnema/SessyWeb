﻿using System.ComponentModel.DataAnnotations;
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

        public override string ToString()
        {
            return $"Time: {Time}, Energy tax: {EnergyTax}, Value added tax: {ValueAddedTax}, PurchaseCompensation: {PurchaseCompensation}, ReturnDeliveryCompensation: {ReturnDeliveryCompensation}, TaxReduction: {TaxReduction}, NetManagementCost: {NetManagementCost}, FixedTransportFee: {FixedTransportFee}, CapacityTransportFee: {CapacityTransportFee}";
        }

        public void Update(Taxes updateInfo)
        {
            Time = updateInfo.Time;
            EnergyTax = updateInfo.EnergyTax;
            ValueAddedTax = updateInfo.ValueAddedTax;
            PurchaseCompensation = updateInfo.PurchaseCompensation;
            ReturnDeliveryCompensation = updateInfo.ReturnDeliveryCompensation;
            TaxReduction = updateInfo.TaxReduction;
            NetManagementCost = updateInfo.NetManagementCost;
            FixedTransportFee = updateInfo.FixedTransportFee;
            CapacityTransportFee = updateInfo.CapacityTransportFee;
        }
    }
}

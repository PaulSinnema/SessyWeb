using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class Taxes : IUpdatable<Taxes>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime? Time { get; set; }
        public double EnergyTax { get; set; }
        public double ValueAddedTax { get; set; }
        public double PurchaseCompensation { get; set; }
        public double ReturnDeliveryCompensation { get; set; }
        public double TaxReduction { get; set; }

        public override string ToString()
        {
            return $"Time: {Time}, Energy tax {EnergyTax}, Value added tax {ValueAddedTax}";
        }

        public void Update(Taxes updateInfo)
        {
            Time = updateInfo.Time;
            EnergyTax = updateInfo.EnergyTax;
            ValueAddedTax = updateInfo.ValueAddedTax;
            PurchaseCompensation = updateInfo.PurchaseCompensation;
            ReturnDeliveryCompensation = updateInfo.ReturnDeliveryCompensation;
            TaxReduction = updateInfo.TaxReduction;
        }
    }
}

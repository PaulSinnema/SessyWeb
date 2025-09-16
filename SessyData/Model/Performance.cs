using Microsoft.EntityFrameworkCore;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    [Index(nameof(Time))]
    public class Performance : IUpdatable<Performance>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public double MarketPrice { get; set; }
        public double BuyingPrice { get; set; }
        public double SmoothedBuyingPrice { get; set; }
        public double SellingPrice { get; set; }
        public double SmoothedSellingPrice { get; set; }
        public double Profit { get; set; }
        public double EstimatedConsumptionPerQuarterHour { get; set; }
        public double ChargeLeft { get; set; }
        public double ChargeNeeded { get; set; }
        public bool Charging { get; set; }
        public bool Discharging { get; set; }
        public double Price => Charging ? BuyingPrice : SellingPrice;
        public double SolarPowerPerQuarterHour { get; set; }
        public double SolarGlobalRadiation { get; set; }
        public double ChargeLeftPercentage { get; set; }
        public string? DisplayState { get; set; }
        public double VisualizeInChart { get; set; }
        public double SmoothedSolarPower { get; set; }

        public void Update(Performance updateInfo)
        {
            this.Copy(updateInfo);
        }
        public override string ToString()
        {
            return $"Buying Price: {BuyingPrice}, Selling Price: {SellingPrice}, Profit: {Profit}, " +
                   $"Estimated Consumption Per Quarter Hour: {EstimatedConsumptionPerQuarterHour}, " +
                   $"Charge Left: {ChargeLeft}, Charge Needed: {ChargeNeeded}, " +
                   $"Charging: {Charging}, Discharging: {Discharging}";
        }
    }
}

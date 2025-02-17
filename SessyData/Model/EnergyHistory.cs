using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    public class EnergyHistory
    {
        [Key]
        public DateTime Time { get; set; }
        public string? Id { get; set; }
        public double ConsumedTariff1 {get; set; }
        public double ConsumedTariff2 { get; set; }
        public double ProducedTariff1 { get; set; }
        public double ProducedTariff2 { get; set; }
        public int TarrifIndicator { get; set; }
        public double Temperature { get; set; }
    }
}

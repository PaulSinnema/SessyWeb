using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class EnergyHistory
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public string? MeterId { get; set; }
        public double ConsumedTariff1 {get; set; }
        public double ConsumedTariff2 { get; set; }
        public double ProducedTariff1 { get; set; }
        public double ProducedTariff2 { get; set; }
        public int TarrifIndicator { get; set; }
        public double Temperature { get; set; }
        public double Price { get; set; }
        public double GlobalRadiation { get; set; }
    }
}

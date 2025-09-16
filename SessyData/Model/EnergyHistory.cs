using Microsoft.EntityFrameworkCore;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    [Index(nameof(Time))]
    public class EnergyHistory : IUpdatable<EnergyHistory>
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
        public double GlobalRadiation { get; set; }

        public void Update(EnergyHistory updateInfo)
        {
            this.Copy(updateInfo);
        }

        public override string ToString()
        {
            return $"Id: {Id}, Time: {Time}";
        }
    }
}

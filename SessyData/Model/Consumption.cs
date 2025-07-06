using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class Consumption : IUpdatable<Consumption>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public double ConsumptionKWh { get; set; }
        public double Temperature { get; set; }
        public double GlobalRadiation { get; set; }
        public double Humidity { get; set; }

        public void Update(Consumption updateInfo)
        {
            Time = updateInfo.Time;
            ConsumptionKWh = updateInfo.ConsumptionKWh;
            Temperature = updateInfo.Temperature;
            GlobalRadiation = updateInfo.GlobalRadiation;
            Humidity = updateInfo.Humidity;
        }

        public override string ToString()
        {
            return $"Id: {Id}, Time: {Time}, ConsumptionKWh: {ConsumptionKWh}, Temperature: {Temperature}, GlobalRadiation: {GlobalRadiation}, Humidity: {Humidity}";
        }
    }
}

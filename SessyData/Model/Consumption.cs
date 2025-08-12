using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    [Index(nameof(Time))]
    public class Consumption : IUpdatable<Consumption>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public DateTime Time { get; set; }
        /// <summary>
        /// This is watts consumed in the last quarter hour, so it is the total consumption in watts for the last 15 minutes.
        /// </summary>
        /// <remarks>
        /// The name suggests that it is the total consumption in kWh, but it is actually the total consumption in watts for the last 15 minutes.
        /// </remarks>
        public double ConsumptionWh { get; set; }
        public double Temperature { get; set; }
        public double GlobalRadiation { get; set; }
        public double Humidity { get; set; }

        public void Update(Consumption updateInfo)
        {
            Time = updateInfo.Time;
            ConsumptionWh = updateInfo.ConsumptionWh;
            Temperature = updateInfo.Temperature;
            GlobalRadiation = updateInfo.GlobalRadiation;
            Humidity = updateInfo.Humidity;
        }

        public override string ToString()
        {
            return $"Id: {Id}, Time: {Time}, ConsumptionKWh: {ConsumptionWh}, Temperature: {Temperature}, GlobalRadiation: {GlobalRadiation}, Humidity: {Humidity}";
        }
    }
}

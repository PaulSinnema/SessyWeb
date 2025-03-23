using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

#nullable disable 

namespace SessyData.Model
{
    /// <summary>
    /// In this class the minute data is stored. After 1 hour this data
    /// is compressed into a SolarEdgeHourData row and removed from this set.
    /// </summary>
    public class SolarEdgeData : IUpdatable<SolarEdgeData>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public string InverterId { get; set; }
        public DateTime Time { get; set; }
        public double Power { get; set; }

        public void Update(SolarEdgeData updateInfo)
        {
            InverterId = updateInfo.InverterId;
            Time = updateInfo.Time;
            Power = updateInfo.Power;
        }
    }
}

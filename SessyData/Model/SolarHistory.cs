using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    public class SolarHistory
    {
        [Key]
        public DateTime Time { get; set; }
        public double GlobalRadiation { get; set; }
        public double GeneratedPower { get; set; }
    }
}


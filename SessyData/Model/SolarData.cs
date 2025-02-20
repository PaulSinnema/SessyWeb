using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class SolarData : IUpdatable<SolarData>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime? Time { get; set; }
        public double GlobalRadiation { get; set; }

        public override string ToString()
        {
            return $"Time: {Time}, Global radiation {GlobalRadiation}";
        }

        public void Update(SolarData updateInfo)
        {
            GlobalRadiation = updateInfo.GlobalRadiation;
        }
    }
}

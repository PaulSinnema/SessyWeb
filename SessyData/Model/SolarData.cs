using Microsoft.EntityFrameworkCore;
using SessyCommon.Attributes;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    [Index(nameof(Time))]
    public class SolarData : IUpdatable<SolarData>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        [SkipCopy]
        public DateTime? Time { get; set; }
        public double GlobalRadiation { get; set; }

        public override string ToString()
        {
            return $"Time: {Time}, Global radiation {GlobalRadiation}";
        }

        public void Update(SolarData updateInfo)
        {
            this.Copy(updateInfo);
        }
    }
}

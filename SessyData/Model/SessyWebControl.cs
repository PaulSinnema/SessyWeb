using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    [Index(nameof(Time))]
    public class SessyWebControl : IUpdatable<SessyWebControl>
    {
        public enum SessyWebControlStatus
        {
            SessyWeb,
            Provider
        }

        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public SessyWebControlStatus Status { get; set; }

        public void Update(SessyWebControl updateInfo)
        {
            Time = updateInfo.Time;
            Status = updateInfo.Status;
        }
    }
}

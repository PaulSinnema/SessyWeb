using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// One version of Charged's (the batteries' own) planned power for one quarter. A row is only
    /// written when the value differs from the newest stored one for that quarter, so what Charged
    /// planned at any moment is the newest row per Time with FetchedAt at or before that moment.
    /// Pruned after Settings.ChargedScheduleKeepDays.
    /// </summary>
    [Index(nameof(Time), nameof(FetchedAt))]
    public class ChargedScheduleQuarter
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>Start of the quarter.</summary>
        public DateTime Time { get; set; }

        /// <summary>Summed power of all batteries (W), Sessy's sign: negative charges, positive discharges.</summary>
        public double PowerW { get; set; }

        /// <summary>When the schedule containing this value was read from the batteries.</summary>
        public DateTime FetchedAt { get; set; }
    }
}

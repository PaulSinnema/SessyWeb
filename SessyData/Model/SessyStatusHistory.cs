using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    public class SessyStatusHistory
    {
        [Key]
        public DateTime Time { get; set; }
        public string? Name { get; set; }
        public String? Status { get; set; }
        public string? StatusDetails { get; set; }
    }
}

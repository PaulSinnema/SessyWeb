using System.ComponentModel.DataAnnotations;

namespace SessyData.Model
{
    public class SessyStatusHistory
    {
        [Key]
        public string? Name { get; set; }
        public DateTime Time { get; set; }
        public String? Status { get; set; }
        public string? StatusDetails { get; set; }
    }
}


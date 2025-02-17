﻿using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class SessyStatusHistory
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public string? Name { get; set; }
        public String? Status { get; set; }
        public string? StatusDetails { get; set; }
    }
}

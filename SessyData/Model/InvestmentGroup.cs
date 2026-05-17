using Microsoft.EntityFrameworkCore;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// An investment group combines related investments (e.g. solar + battery)
    /// into a single ROI calculation entity.
    /// </summary>
    [Index(nameof(Name), IsUnique = true)]
    public class InvestmentGroup : IUpdatable<InvestmentGroup>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Unique name of the group (e.g. "Zonne-energie systeem").
        /// Referenced by Investment.InvestmentGroupId.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Category of this investment group — determines which savings
        /// calculation applies to all investments in this group.
        /// </summary>
        public InvestmentCategory Category { get; set; } = InvestmentCategory.Other;

        /// <summary>
        /// Optional description.
        /// </summary>
        public string? Description { get; set; }

        public void Update(InvestmentGroup updateInfo)
        {
            this.Copy(updateInfo);
        }

        public override string ToString() => $"Id: {Id}, Name: {Name}";
    }
}
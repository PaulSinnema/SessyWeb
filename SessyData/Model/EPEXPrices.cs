using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class EPEXPrices : IUpdatable<EPEXPrices>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)] // Auto-increment
        public int Id { get; set; }
        public DateTime Time { get; set; }
        public double? Price { get; set; }

        public override string ToString()
        {
            return $"Time: {Time}, Price: {Price}";
        }

        public void Update(EPEXPrices updateInfo)
        {
            Price = updateInfo.Price;
        }
    }
}

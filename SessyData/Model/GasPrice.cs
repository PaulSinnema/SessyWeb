using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    /// <summary>
    /// Stores the daily TTF day-ahead gas market price (EUR/m³, excl. taxes).
    /// One record per day. The all-in consumer price is calculated on demand using the Taxes table.
    /// </summary>
    public class GasPrice : IUpdatable<GasPrice>
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Date for which this price applies (date only, time = 00:00:00).
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// TTF day-ahead market price in EUR per m³ (excl. taxes and supplier markup).
        /// Source: Enever.nl (EGSI = End of Gas-Day Spot Index).
        /// </summary>
        public double MarketPriceEurPerM3 { get; set; }

        public void Update(GasPrice updateInfo)
        {
            MarketPriceEurPerM3 = updateInfo.MarketPriceEurPerM3;
        }
    }
}
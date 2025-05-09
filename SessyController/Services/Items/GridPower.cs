using SessyData.Model;

namespace SessyController.Services.Items
{
    /// <summary>
    /// This class is used to calculate the total consumption and production using
    /// the P1 Meter information.
    /// </summary>
    public class GridPower
    {
        private double _cons1;
        private double _cons2;
        private double _prod1;
        private double _prod2;
        private EnergyHistory _history1;
        private EnergyHistory _history2;

        /// <summary>
        /// Given 2 EnergyHistory rows where history1 is older than history2 calculates net grid consumption or production
        /// </summary>
        /// <param name="currentHistory">A history row</param>
        /// <param name="previousHistory">The previous history row</param>
        public GridPower(EnergyHistory currentHistory, EnergyHistory previousHistory)
        {
            _history1 = currentHistory;
            _history2 = previousHistory;

            if ((currentHistory.Time - previousHistory.Time).Hours <= 0)
                throw new InvalidOperationException($"Dates be in descending order and not be the same. currentHistory {currentHistory} <= previousHistory {previousHistory}");

            _cons1 = currentHistory.ConsumedTariff1 - previousHistory.ConsumedTariff1;
            _cons2 = currentHistory.ConsumedTariff2 - previousHistory.ConsumedTariff2;
            _prod1 = currentHistory.ProducedTariff1 - previousHistory.ProducedTariff1;
            _prod2 = currentHistory.ProducedTariff2 - previousHistory.ProducedTariff2;
        }

        /// <summary>
        /// Total cost. Negative is what you have to pay. Positive is what you receive.
        /// </summary>
        public double Total => -_cons1 + -_cons2 +  _prod1 + _prod2;

        /// <summary>
        /// For financial reports we need to inverse the sign.
        /// </summary>
        public double TotalInversed => -Total;

        /// <summary>
        /// Total consumption
        /// </summary>
        public double TotalConsumed => _cons1 + _cons2;

        /// <summary>
        /// Total production.
        /// </summary>
        public double TotalProduced => _prod1 + _prod2;

        /// <summary>
        /// Is this item a consumer of producer of energy?
        /// </summary>
        public bool IsConsumer => Total < 0.00;
    }
}

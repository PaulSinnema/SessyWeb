namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Battery saving.
    /// Only charges and discharges when the price spread is wide enough to justify the extra wear.
    /// Achieved by doubling the effective cycle cost, which raises the profitability threshold.
    /// Extends battery lifetime at the cost of lower arbitrage revenue.
    /// </summary>
    public sealed class BatterySavingStrategy : IBatteryOptimizationStrategy
    {
        /// <summary>Multiplier on top of the derived cycle cost. 2.0 = require a twice as wide spread.</summary>
        private const double CycleCostMultiplier = 2.0;

        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            var options = context.Options with
            {
                CycleCostEurPerKWh = context.Options.CycleCostEurPerKWh * CycleCostMultiplier
            };

            var result = BatteryGreedyPlanner.Solve(
                context.PricePoints,
                context.Spec,
                options,
                context.SocBounds);

            return Task.FromResult(result);
        }
    }
}
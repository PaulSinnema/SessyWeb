namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Balanced strategy.
    /// Same planner as profit maximization, but with a modest surcharge on the cycle cost so the
    /// battery trades a little less eagerly. Sits between profit maximization and battery saving.
    /// </summary>
    public sealed class BalancedStrategy : IBatteryOptimizationStrategy
    {
        /// <summary>Multiplier on top of the derived cycle cost. 1.5 = require a 50% wider spread.</summary>
        private const double CycleCostMultiplier = 1.5;

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
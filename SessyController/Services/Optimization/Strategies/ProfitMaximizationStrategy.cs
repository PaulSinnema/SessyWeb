namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Profit maximization.
    /// Charges in the cheapest quarters and discharges in the most expensive ones, as long as the
    /// spread covers the round-trip losses and the cycle cost. Export to the grid is allowed.
    /// </summary>
    public sealed class ProfitMaximizationStrategy : IBatteryOptimizationStrategy
    {
        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            var result = BatteryGreedyPlanner.Solve(
                context.PricePoints,
                context.Spec,
                context.Options,
                context.SocBounds);

            return Task.FromResult(result);
        }
    }
}
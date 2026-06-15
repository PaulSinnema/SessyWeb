namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Balanced strategy.
    /// With the grid-balance solver the model already decides optimally whether to store
    /// solar surplus or export it, so no artificial headroom reservation is applied here.
    /// This strategy is currently identical to profit maximization; it exists as a
    /// placeholder for future balance-oriented tuning (e.g. extra cycle-cost weighting).
    /// </summary>
    public sealed class BalancedStrategy : IBatteryOptimizationStrategy
    {
        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            var result = BatteryArbitrageMilp.Solve(
                context.PricePoints,
                context.Spec,
                context.Options,
                context.SocBounds);

            return Task.FromResult(result);
        }
    }
}
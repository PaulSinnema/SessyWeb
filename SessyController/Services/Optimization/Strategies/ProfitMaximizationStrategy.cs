namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Profit maximization strategy.
    /// Uses the grid-balance objective: minimise total grid cost over the horizon
    /// (import*buy − export*sell + cycleCost*discharge), charging when cheap and
    /// discharging / self-consuming when expensive.
    /// </summary>
    public sealed class ProfitMaximizationStrategy : IBatteryOptimizationStrategy
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
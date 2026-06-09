namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Self-consumption strategy.
    /// Maximises own use of generated solar energy.
    /// Discharge is bounded to household load — no export to grid.
    /// Suitable for post-saldering scenarios where export has little value.
    /// </summary>
    public sealed class SelfConsumptionStrategy : IBatteryOptimizationStrategy
    {
        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            var result = SelfConsumptionMilp.Solve(
                context.PricePoints,
                context.Spec,
                context.Options,
                context.SocBounds);

            return Task.FromResult(result);
        }
    }
}
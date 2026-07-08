namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Self-consumption.
    /// Maximises own use of generated solar energy: the battery stores surplus and covers the
    /// household load, but never exports to the grid. Suitable once net metering (saldering) ends
    /// and exported energy is worth far less than imported energy.
    /// </summary>
    public sealed class SelfConsumptionStrategy : IBatteryOptimizationStrategy
    {
        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            var options = context.Options with { AllowExport = false };

            var result = BatteryGreedyPlanner.Solve(
                context.PricePoints,
                context.Spec,
                options,
                context.SocBounds);

            return Task.FromResult(result);
        }
    }
}
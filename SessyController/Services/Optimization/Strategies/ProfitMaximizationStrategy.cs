namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Profit maximization strategy.
    /// Charges at low prices, discharges at high prices.
    /// Uses the pure arbitrage objective: discharge * max(buy, sell) - gridCharge * buy - cycleCost.
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
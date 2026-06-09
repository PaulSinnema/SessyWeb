namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Battery saving strategy.
    /// Only charges/discharges when the price spread is large enough to justify
    /// battery wear. Uses a higher effective cycle cost to raise the threshold
    /// for profitable charge/discharge decisions.
    /// Extends battery lifetime at the cost of lower arbitrage revenue.
    /// </summary>
    public sealed class BatterySavingStrategy : IBatteryOptimizationStrategy
    {
        // Multiplier on top of the configured cycle cost.
        // 2.0 = only trade when spread > 2× normal cycle cost.
        private const double CycleCostMultiplier = 2.0;

        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            // Increase effective cycle cost so the solver only charges/discharges
            // when the price spread is large enough to compensate for extra battery wear.
            var higherCostOptions = context.Options with
            {
                CycleCostEurPerKWh = context.Options.CycleCostEurPerKWh * CycleCostMultiplier
            };

            var adjustedContext = context with { Options = higherCostOptions };

            var result = BatteryArbitrageMilp.Solve(
                adjustedContext.PricePoints,
                adjustedContext.Spec,
                adjustedContext.Options,
                adjustedContext.SocBounds);

            return Task.FromResult(result);
        }
    }
}
namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Balanced strategy.
    /// Profit-first but always reserves headroom for the total expected solar surplus
    /// across the planning horizon, not just the single largest quarter.
    /// This prevents the battery from being fully charged when solar is about to peak.
    /// </summary>
    public sealed class BalancedStrategy : IBatteryOptimizationStrategy
    {
        // Reserve headroom for this fraction of total expected solar surplus.
        // 1.0 = reserve space for all expected solar; 0.5 = half.
        private const double SolarHeadroomFraction = 1.0;

        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            // Tighten maxSoc per quarter: subtract total remaining solar surplus
            // (not just the single largest quarter) so the battery always has room
            // to absorb upcoming solar production.
            var tightenedBounds = context.SocBounds.Select((b, idx) =>
            {
                // Sum all solar surplus from this quarter onwards.
                double remainingSolarKWh = context.PricePoints
                    .Skip(idx)
                    .Where(p => p.NetLoadWh < 0.0)
                    .Sum(p => -p.NetLoadWh / 1000.0);

                double tightenedMax = Math.Max(
                    b.MinSocKWh,
                    b.MaxSocKWh - remainingSolarKWh * SolarHeadroomFraction);

                return new SocBound(b.Time, b.MinSocKWh, tightenedMax);
            }).ToList();

            var tightenedContext = context with { SocBounds = tightenedBounds };

            var result = BatteryArbitrageMilp.Solve(
                tightenedContext.PricePoints,
                tightenedContext.Spec,
                tightenedContext.Options,
                tightenedContext.SocBounds);

            return Task.FromResult(result);
        }
    }
}
using SessyData.Model;

namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Every strategy is the greedy planner with different options: a surcharge on the cycle cost
    /// (trade less eagerly) and whether export is allowed.
    /// </summary>
    public sealed class GreedyStrategy : IBatteryOptimizationStrategy
    {
        private readonly double _cycleCostMultiplier;
        private readonly bool _allowExport;

        /// <param name="cycleCostMultiplier">Multiplier on the derived cycle cost; 2.0 requires a twice as wide spread.</param>
        /// <param name="allowExport">False keeps the battery from exporting to the grid.</param>
        public GreedyStrategy(double cycleCostMultiplier = 1.0, bool allowExport = true)
        {
            _cycleCostMultiplier = cycleCostMultiplier;
            _allowExport = allowExport;
        }

        /// <summary>
        /// Profit maximization trades whenever the spread covers losses and cycle cost; self-consumption
        /// never exports (for when exported energy is worth far less than imported); balanced asks a
        /// 50% wider spread; battery saving twice the spread, for a longer battery life.
        /// </summary>
        public static GreedyStrategy For(OptimizationStrategy strategy) => strategy switch
        {
            OptimizationStrategy.ProfitMaximization => new(),
            OptimizationStrategy.SelfConsumption => new(allowExport: false),
            OptimizationStrategy.BatterySaving => new(cycleCostMultiplier: 2.0),
            _ => new(cycleCostMultiplier: 1.5)
        };

        public Task<PlanResult?> SolveAsync(SolveContext context)
        {
            var options = context.Options with
            {
                CycleCostEurPerKWh = context.Options.CycleCostEurPerKWh * _cycleCostMultiplier,
                AllowExport = context.Options.AllowExport && _allowExport
            };

            return Task.FromResult(BatteryGreedyPlanner.Solve(
                context.PricePoints,
                context.Spec,
                options,
                context.SocBounds,
                context.Trace));
        }
    }
}

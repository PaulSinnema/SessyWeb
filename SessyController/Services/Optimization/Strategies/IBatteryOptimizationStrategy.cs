using SessyController.Services.Optimization;

namespace SessyController.Services.Optimization.Strategies
{
    /// <summary>
    /// Context passed to the optimization strategy containing everything needed to solve.
    /// </summary>
    public sealed record SolveContext(
        IReadOnlyList<PricePoint> PricePoints,
        BatterySpec Spec,
        SessyOptions Options,
        IReadOnlyList<SocBound> SocBounds
    );

    /// <summary>
    /// Encapsulates the battery optimization objective.
    /// Each strategy receives the same SolveContext and returns a PlanResult.
    /// The base class handles all plan state, persistence and runtime guards.
    /// </summary>
    public interface IBatteryOptimizationStrategy
    {
        /// <summary>
        /// Solve the optimization problem for the given context.
        /// Returns null when no feasible solution was found.
        /// </summary>
        Task<PlanResult?> SolveAsync(SolveContext context);
    }
}
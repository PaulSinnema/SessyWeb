using SessyController.Services.Optimization;
using SessyController.Services.Optimization.Strategies;

namespace SessyController.Services
{
    /// <summary>
    /// Shadow comparison: runs the optimal DP planner against the greedy plan on identical inputs
    /// and logs the objective gap, so the value of a DP planner (openstaand punt 4) can be judged on
    /// live data over weeks. It NEVER drives the batteries — greedy stays at the wheel.
    ///
    /// Both planners maximize the same reported objective (undiscounted prices + carry-forward), so a
    /// positive delta is real headroom. Logging is gated to once per 15 minutes and carries a running
    /// average, keeping the Warning log readable while still accumulating the signal.
    /// </summary>
    internal static class PlannerShadow
    {
        private static readonly object _lock = new();
        private static DateTime _lastAt = DateTime.MinValue;
        private static double _sumDelta;
        private static int _count;

        public static void Compare(SolveContext ctx, PlanResult? greedy, DateTime now, Action<string> log)
        {
            if (ctx == null || greedy == null) return;
            try
            {
                var dp = BatteryDpPlanner.Solve(ctx.PricePoints, ctx.Spec, ctx.Options, ctx.SocBounds);
                if (dp == null) return;

                double delta = dp.ObjectiveEur - greedy.ObjectiveEur;
                double avg; int count;
                lock (_lock)
                {
                    _sumDelta += delta; _count++;
                    if ((now - _lastAt).TotalMinutes < 15) return;
                    _lastAt = now; avg = _sumDelta / _count; count = _count;
                }

                log($"PLANNER_SHADOW[{now:dd-MM HH:mm}]: greedy {greedy.ObjectiveEur:F4} vs DP {dp.ObjectiveEur:F4} " +
                    $"-> delta {delta:F4} EUR/plan (avg {avg:F4} over {count} plans)");
            }
            catch (Exception ex)
            {
                log($"PLANNER_SHADOW failed: {ex.Message}");
            }
        }
    }
}

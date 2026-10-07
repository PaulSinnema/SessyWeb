using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    public class TmpEmptyProbeTests
    {
        private readonly ITestOutputHelper _output;
        public TmpEmptyProbeTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Probe()
        {
            var start = new DateTime(2026, 10, 6, 13, 0, 0);
            var peakStart = start.Date.AddHours(17).AddMinutes(45);
            var peakEnd = start.Date.AddHours(20).AddMinutes(15);
            var points = new List<PricePoint>();
            for (var t = start; t < start.Date.AddDays(1); t = t.AddMinutes(15))
            {
                (double buy, double sell) =
                    t < start.Date.AddHours(14).AddMinutes(15) ? (0.295, 0.251) :
                    t < start.Date.AddHours(15) ? (0.313, 0.269) :
                    t < peakStart ? (0.32, 0.276) :
                    t < peakEnd ? (0.60, 0.556) :
                    (0.42, 0.376);
                points.Add(new PricePoint(t, buy, sell, NetLoadWh: 150.0, SolarSurplusWh: 0.0));
            }
            var bounds = points.Select(p => new SocBound(p.Start, 0.81, 16.2)).ToList();
            var cap = new DischargeCapability(PlateauW: 4669, KneeSoc: 0.30, Samples: 20);
            foreach (bool shift in new[] { true, false })
            {
                var spec = new BatterySpec(16.2, 0.467, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                    ChargeEfficiency: 0.87, DischargeEfficiency: 0.90, DischargeCapability: cap);
                var opt = new SessyOptions(QuarterMinutes: 15, CycleCostEurPerKWh: 0.001, AllowExport: true, AllowShift: shift);
                var plan = BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
                _output.WriteLine($"shift {shift} objective {plan.ObjectiveEur:F3}");
                foreach (var p in plan.Plan.Where(p => p.Start.Hour >= 17))
                {
                    double capKW = Math.Min(5.1, cap.PowerW(p.SocStartKWh / 16.2) / 1000.0);
                    _output.WriteLine($"  {p.Start:HH:mm} {p.Mode,-12} ch {p.ChargeKW:F2} dis {p.DischargeKW:F2} cap {capKW:F2} soc {p.SocStartKWh:F3}->{p.SocEndKWh:F3}");
                }
            }
        }
    }
}

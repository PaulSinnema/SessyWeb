using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The recorder is only useful if what comes back is what went in — a replay against a
    /// half-deserialised spec would send the investigation off in the wrong direction, which is
    /// exactly the failure it exists to prevent.
    /// </summary>
    public class SolveInputRecorderTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"sessy_solve_{Guid.NewGuid():N}");

        private static readonly DateTime Now = new(2026, 8, 10, 17, 0, 0);

        private static (List<PricePoint> Points, BatterySpec Spec, SessyOptions Options, List<SocBound> Bounds) Sample()
        {
            var points = new List<PricePoint>
            {
                new(Now, 0.30, 0.26, NetLoadWh: 120, SolarSurplusWh: 0,
                    MaxChargeKW: 5.0, MaxDischargeKW: 4.1, ReserveOnly: false,
                    TemperatureC: 21.5, Temperature48hC: 19.0),
                new(Now.AddMinutes(15), 0.31, 0.27, NetLoadWh: -400, SolarSurplusWh: 400)
            };

            var spec = new BatterySpec(
                CapacityKWh: 16.2, InitialSocKWh: 14.7, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.91, DischargeEfficiency: 0.91,
                ChargeTaper: new ChargeTaper(1.05, 0.54, 0.023, 0.015, 312),
                Efficiency: new EfficiencyCurve(0.8415, 0.058, 0.9278, 0.0645, 1667),
                DischargeCapability: new DischargeCapability(4121, 0.20, 748));

            var options = new SessyOptions(15, 0.0862, true, 0.003, 0.1291, true);

            var bounds = new List<SocBound>
            {
                new(Now, 5.31, 16.2),
                new(Now.AddMinutes(15), 5.31, 16.2)
            };

            return (points, spec, options, bounds);
        }

        [Fact]
        public void Nothing_is_written_while_the_recorder_is_off()
        {
            Environment.SetEnvironmentVariable(SolveInputRecorder.EnableVariable, null);

            var (points, spec, options, bounds) = Sample();

            Assert.Null(SolveInputRecorder.TryWrite(_directory, points, spec, options, bounds, Now));
            Assert.False(Directory.Exists(_directory));
        }

        [Fact]
        public void What_is_recorded_replays_identically()
        {
            Environment.SetEnvironmentVariable(SolveInputRecorder.EnableVariable, "1");

            try
            {
                var (points, spec, options, bounds) = Sample();

                var path = SolveInputRecorder.TryWrite(_directory, points, spec, options, bounds, Now);
                Assert.NotNull(path);

                var read = SolveInputRecorder.Read(path!);
                Assert.NotNull(read);

                // Records compare by value, so this covers every field including the fitted
                // taper, efficiency curve and discharge capability.
                Assert.Equal(points, read!.PricePoints);
                Assert.Equal(spec, read.Spec);
                Assert.Equal(options, read.Options);
                Assert.Equal(bounds, read.SocBounds);

                // And the point of it all: the same input gives the same plan.
                var original = BatteryGreedyPlanner.Solve(points, spec, options, bounds);
                var replayed = BatteryGreedyPlanner.Solve(read.PricePoints, read.Spec, read.Options, read.SocBounds);

                Assert.NotNull(original);
                Assert.NotNull(replayed);
                Assert.Equal(original!.ObjectiveEur, replayed!.ObjectiveEur, 6);
                Assert.Equal(original.Plan.Count, replayed.Plan.Count);
            }
            finally
            {
                Environment.SetEnvironmentVariable(SolveInputRecorder.EnableVariable, null);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}

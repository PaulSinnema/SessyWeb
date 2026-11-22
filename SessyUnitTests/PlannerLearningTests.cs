using SessyController.Services;
using SessyController.Services.Items;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Covers the two fits that replace hand-picked planner numbers with measured ones.
    ///
    /// The discount fit is built to be invertible: a forecast whose relative error follows
    /// e(h) = r*h / (1 + r*h) is exactly what a discount curve 1 / (1 + r*h) describes, so a
    /// synthetic series generated from a known r must come back as that same r. That is what
    /// these tests assert — not a range, the number itself.
    /// </summary>
    public class PlannerLearningTests
    {
        private static readonly DateTime Now = new(2026, 7, 30, 3, 0, 0);

        /// <summary>Actual net load per quarter (Wh); every sample uses the same value so the
        /// scale the error is expressed against is exactly this.</summary>
        private const double ActualNetWh = 1000.0;

        /// <summary>Comfortably above MinBucketSamples.</summary>
        private const int SamplesPerBucket = 50;

        /// <summary>
        /// Builds snapshots whose net-load error at lead h is e(h) = rate*h / (1 + rate*h) of a
        /// typical quarter, spread over enough days to clear the 21-day minimum.
        /// </summary>
        private static (List<ForecastSnapshot> Snapshots, Dictionary<DateTime, double> Actuals)
            BuildSeries(double rate, IReadOnlyList<int> leads, int days = 22)
        {
            var snapshots = new List<ForecastSnapshot>();
            var actuals = new Dictionary<DateTime, double>();

            var first = Now.AddDays(-days);
            int index = 0;

            foreach (int lead in leads)
            {
                double relative = rate * lead / (1.0 + rate * lead);
                double error = relative * ActualNetWh;

                for (int i = 0; i < SamplesPerBucket; i++)
                {
                    // Spread the samples of every bucket across the whole window, so the span
                    // covers the required number of days whichever bucket is present.
                    var time = first.AddMinutes(15 * index++)
                                    .AddDays(i * (days - 1) / (double)SamplesPerBucket);

                    actuals[time] = ActualNetWh;

                    snapshots.Add(new ForecastSnapshot
                    {
                        Time = time,
                        LeadHours = lead,
                        // NetLoad = consumption * 0.25 - solar, so consumption alone carries it.
                        ConsumptionForecastW = (ActualNetWh + error) * 4.0,
                        SolarForecastW = 0.0
                    });
                }
            }

            return (snapshots, actuals);
        }

        // ── Discount ──────────────────────────────────────────────────────────

        [Fact]
        public void Discount_RecoversTheRateItsErrorCurveWasBuiltFrom()
        {
            var (snapshots, actuals) = BuildSeries(rate: 0.01, leads: [0, 1, 6, 24]);

            var (value, pinned, _) = PlannerLearningService.FitDiscount(snapshots, actuals);

            Assert.Equal(0.01, value!.Value, 4);
            Assert.False(pinned);
        }

        [Fact]
        public void Discount_SubtractsTheErrorAlreadyPresentAtZeroLead()
        {
            // A model that is off by a fixed amount even at zero lead is not forecast uncertainty
            // and no discount can buy it off, so the fitted rate must not move.
            var (snapshots, actuals) = BuildSeries(rate: 0.01, leads: [0, 1, 6, 24]);

            foreach (var snapshot in snapshots)
                snapshot.ConsumptionForecastW += 100.0 * 4.0;   // +100 Wh on every lead, zero included

            var (value, _, _) = PlannerLearningService.FitDiscount(snapshots, actuals);

            Assert.Equal(0.01, value!.Value, 4);
        }

        [Fact]
        public void Discount_PinsAtTheUpperBoundWhenForecastsAreHopeless()
        {
            var (snapshots, actuals) = BuildSeries(rate: 1.0, leads: [0, 1, 6, 24]);

            var (value, pinned, note) = PlannerLearningService.FitDiscount(snapshots, actuals);

            Assert.Equal(0.03, value!.Value, 6);
            Assert.True(pinned);
            Assert.Contains("clamped", note);
        }

        [Fact]
        public void Discount_KeepsTheConfiguredValueBeforeTwentyOneDays()
        {
            var (snapshots, actuals) = BuildSeries(rate: 0.02, leads: [0, 1, 6, 24], days: 5);

            var (value, pinned, note) = PlannerLearningService.FitDiscount(snapshots, actuals);

            // Null, not a default: writing 1%/hour over a value tuned on measurements would be a
            // step backwards, and rewriting it nightly would make the field impossible to correct.
            Assert.Null(value);
            Assert.False(pinned);
            Assert.Contains("kept", note);
        }

        [Fact]
        public void Discount_KeepsTheConfiguredValueWhenNoLeadBucketHasEnoughSamples()
        {
            var (snapshots, actuals) = BuildSeries(rate: 0.02, leads: [0, 1, 6, 24]);

            // Keep the 21-day span but leave every non-zero bucket under the sample threshold.
            var thinned = snapshots
                .GroupBy(s => s.LeadHours)
                .SelectMany(g => g.Key == 0 ? g : g.Take(5))
                .ToList();

            var (value, _, note) = PlannerLearningService.FitDiscount(thinned, actuals);

            Assert.Null(value);
            Assert.Contains("kept", note);
        }

        [Fact]
        public void Discount_NetsSolarErrorAgainstConsumptionError()
        {
            // Consumption forecast too high by 200 Wh and solar forecast too high by the same
            // 200 Wh is a perfect net-load forecast. Charging both as uncertainty would inflate
            // the discount; the planner plans against the net, so the errors have to cancel.
            var (snapshots, actuals) = BuildSeries(rate: 0.02, leads: [0, 1, 6, 24]);

            foreach (var snapshot in snapshots)
            {
                snapshot.ConsumptionForecastW = (ActualNetWh + 200.0) * 4.0;
                snapshot.SolarForecastW = 200.0;
            }

            var (value, pinned, note) = PlannerLearningService.FitDiscount(snapshots, actuals);

            // No error left to buy off, so the fit lands on the lower bound and says so.
            Assert.Equal(0.005, value!.Value, 6);
            Assert.True(pinned);
            Assert.Contains("clamped", note);
        }

        // ── Supporting pieces ─────────────────────────────────────────────────

        [Theory]
        [InlineData(0.0, 0)]
        [InlineData(0.9, 0)]
        [InlineData(1.0, 1)]
        [InlineData(5.9, 4)]
        [InlineData(24.0, 24)]
        [InlineData(47.0, 36)]
        [InlineData(96.0, 48)]
        public void LeadBucket_RoundsDownToTheLadder(double leadHours, int expected)
            => Assert.Equal(expected, ForecastSnapshot.BucketFor(leadHours));

        [Theory]
        [InlineData(0.0, 1.0)]
        [InlineData(50.0, 3.0)]
        [InlineData(100.0, 5.0)]
        [InlineData(25.0, 2.0)]
        public void Percentile_InterpolatesBetweenNeighbours(double percentile, double expected)
            => Assert.Equal(expected, Stats.Percentile([1.0, 2.0, 3.0, 4.0, 5.0], percentile), 6);

        [Fact]
        public void Percentile_HandlesEmptyAndSingleValueLists()
        {
            Assert.Equal(0.0, Stats.Percentile([], 50.0));
            Assert.Equal(7.0, Stats.Percentile([7.0], 90.0));
        }
    }
}

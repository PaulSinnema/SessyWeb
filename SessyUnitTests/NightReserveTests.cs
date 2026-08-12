using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Covers MilpServiceBase.ComputeMinSocWh — the floor the planner may not sell through.
    ///
    /// The case that made this testable: the price horizon always ends at 23:45 tomorrow
    /// (EPEXPricesService), so a forward scan for "the next no-solar window" runs out of quarters
    /// somewhere in the last evening. Summing only what is visible then reserved a fraction of a
    /// night, and nothing at all at the final quarter, which let the plan sell down to 3% with a
    /// full night still ahead.
    ///
    /// Every case computes from index 0, so the scan covers everything after the first entry.
    /// </summary>
    public class NightReserveTests
    {
        private const double CapacityWh = 16200.0;

        /// <summary>Production value, learned by PlannerLearningService as P80 of measured nights.</summary>
        private const double NightCapRatio = 0.3279;

        private const double SafetyFactor = 1.10;

        /// <summary>Measured household draw per quarter overnight.</summary>
        private const double LoadWh = 104.0;

        private static double FullNightWh => CapacityWh * NightCapRatio;

        /// <summary>Quarters of household draw with no solar, e.g. an evening or a night.</summary>
        private static IEnumerable<double> Load(int quarters, double wh = LoadWh) =>
            Enumerable.Repeat(wh, quarters);

        /// <summary>Quarters of solar surplus — net load is negative.</summary>
        private static IEnumerable<double> Solar(int quarters, double wh = 400.0) =>
            Enumerable.Repeat(-wh, quarters);

        private static double Compute(IReadOnlyList<double> netLoads, int index) =>
            MilpServiceBase.ComputeMinSocWh(netLoads, index, CapacityWh, NightCapRatio, SafetyFactor);

        [Fact]
        public void Last_quarter_of_the_horizon_reserves_a_whole_night()
        {
            // Nothing follows the final quarter, so the visible sum is 0 — the plan used to be free
            // to arrive at midnight empty.
            var netLoads = Load(20).ToList();

            Assert.Equal(FullNightWh, Compute(netLoads, netLoads.Count - 1), 6);
        }

        [Fact]
        public void The_truncated_last_evening_reserves_a_whole_night_not_the_visible_part()
        {
            // 24 quarters of evening left inside the horizon: 2746 Wh visible against a real night
            // of 5312 Wh.
            var netLoads = Load(25).ToList();
            double visibleOnly = 24 * LoadWh * SafetyFactor;

            double reserve = Compute(netLoads, 0);

            Assert.True(reserve > visibleOnly);
            Assert.Equal(FullNightWh, reserve, 6);
        }

        [Fact]
        public void A_scan_that_reaches_the_next_sunrise_is_unchanged()
        {
            // Tonight, then tomorrow's sun, then tomorrow's evening. The scan breaks on the second
            // solar quarter (v1.0.48: reserving across a solar window kept energy for the evening
            // after the next one), so nothing is truncated and only tonight counts.
            // The first solar quarter lands in solarBeforeWh before the break — existing behaviour,
            // asserted here so a rewrite cannot change it unnoticed.
            var netLoads = Load(21)
                .Concat(Solar(30))
                .Concat(Load(40))
                .ToList();

            double expected = (20 * LoadWh - 400.0) * SafetyFactor;

            Assert.Equal(expected, Compute(netLoads, 0), 6);
        }

        [Fact]
        public void Solar_before_the_truncated_night_lowers_the_reserve()
        {
            // 8 quarters of sun at 400 Wh refill 3200 Wh before the night starts, so only the rest
            // of the learned need has to be held back.
            var netLoads = Load(1).Concat(Solar(8)).Concat(Load(20)).ToList();

            double expected = (FullNightWh - 3200.0) * SafetyFactor;

            Assert.Equal(expected, Compute(netLoads, 0), 6);
        }

        [Fact]
        public void Enough_solar_before_the_truncated_night_holds_nothing_back()
        {
            // The sun covers more than a whole night — the battery need not carry any of it.
            var netLoads = Load(1).Concat(Solar(30, 1000.0)).Concat(Load(20)).ToList();

            Assert.Equal(0.0, Compute(netLoads, 0), 6);
        }

        [Fact]
        public void The_cap_binds()
        {
            // A night far heavier than the learned one still cannot reserve above the cap.
            var netLoads = Load(61, 900.0).ToList();

            Assert.Equal(FullNightWh, Compute(netLoads, 0), 6);
        }

        [Fact]
        public void A_horizon_that_is_all_solar_reserves_nothing()
        {
            var netLoads = Solar(40).ToList();

            Assert.Equal(0.0, Compute(netLoads, 0), 6);
        }
    }
}

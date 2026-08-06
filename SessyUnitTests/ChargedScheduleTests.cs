using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Charged's own schedule, as read from the batteries.
    ///
    /// The version this replaces read a single battery and presented that power as the system
    /// total, so the chart drew everything at roughly a third of its real height. The sign
    /// convention is Sessy's throughout: negative charges, positive discharges.
    /// </summary>
    public class ChargedScheduleTests
    {
        private static readonly DateTime Start = new(2026, 8, 6, 12, 0, 0);
        private const double CapacityWh = 16200.0;

        static ChargedScheduleTests()
        {
            // DynamicScheduleItem converts its unix timestamps through TimeZoneService, which keeps
            // the zone in a static field. Constructing one instance is what fills it.
            _ = new TimeZoneService(Options.Create(new SettingsConfig { Timezone = "Europe/Amsterdam" }));
        }

        private static long Unix(DateTime time) =>
            new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time)).ToUnixTimeSeconds();

        private static DynamicScheduleItem Item(DateTime start, DateTime end, int powerW) =>
            new() { StartTimeUnix = Unix(start), EndTimeUnix = Unix(end), Power = powerW };

        // ── Blocks to quarters ────────────────────────────────────────────────

        [Fact]
        public void An_hour_block_fills_four_quarters_at_the_same_power()
        {
            var quarters = ChargedScheduleService.ToQuarters(
                new[] { Item(Start, Start.AddHours(1), -2000) });

            Assert.Equal(4, quarters.Count);

            // Power is a rate, not an amount: every quarter of that hour runs at 2000 W.
            foreach (var value in quarters.Values)
                Assert.Equal(-2000.0, value);
        }

        [Fact]
        public void Discharging_keeps_its_positive_sign()
        {
            var quarters = ChargedScheduleService.ToQuarters(
                new[] { Item(Start, Start.AddMinutes(30), 1500) });

            Assert.Equal(2, quarters.Count);
            Assert.All(quarters.Values, v => Assert.Equal(1500.0, v));
        }

        [Fact]
        public void Batteries_are_summed_not_overwritten()
        {
            // Three batteries each planning 2000 W is 6000 W of system power.
            var one = ChargedScheduleService.ToQuarters(new[] { Item(Start, Start.AddMinutes(15), -2000) });
            var two = ChargedScheduleService.ToQuarters(new[] { Item(Start, Start.AddMinutes(15), -2000) });
            var three = ChargedScheduleService.ToQuarters(new[] { Item(Start, Start.AddMinutes(15), -2000) });

            var total = new Dictionary<DateTime, double>();

            foreach (var part in new[] { one, two, three })
                foreach (var (time, power) in part)
                {
                    total.TryGetValue(time, out var running);
                    total[time] = running + power;
                }

            Assert.Equal(-6000.0, total[Start]);
        }

        [Fact]
        public void An_empty_schedule_produces_no_quarters()
        {
            var quarters = ChargedScheduleService.ToQuarters(Array.Empty<DynamicScheduleItem>());

            Assert.Empty(quarters);
        }

        // ── SOC projection ────────────────────────────────────────────────────

        [Fact]
        public void Charging_raises_the_projected_soc_by_the_stored_energy()
        {
            var schedule = new Dictionary<DateTime, double> { [Start] = -4000.0 };

            var soc = ChargedScheduleService.Project(
                schedule, Start, socWh: 5000.0, capacityWh: CapacityWh,
                chargeEfficiency: 0.95, dischargeEfficiency: 0.95);

            // 4000 W for a quarter is 1000 Wh on the AC side, 950 Wh stored.
            Assert.Equal(5950.0, soc[Start], 1);
        }

        [Fact]
        public void Discharging_drains_more_than_it_delivers()
        {
            var schedule = new Dictionary<DateTime, double> { [Start] = 4000.0 };

            var soc = ChargedScheduleService.Project(
                schedule, Start, socWh: 5000.0, capacityWh: CapacityWh,
                chargeEfficiency: 0.95, dischargeEfficiency: 0.95);

            // Delivering 1000 Wh takes 1000 / 0.95 = 1052.6 Wh out of the store.
            Assert.Equal(5000.0 - 1000.0 / 0.95, soc[Start], 1);
        }

        [Fact]
        public void The_projection_stays_between_empty_and_full()
        {
            var schedule = new Dictionary<DateTime, double>
            {
                [Start] = -100000.0,                    // way more than fits
                [Start.AddMinutes(15)] = 100000.0,      // way more than is there
            };

            var soc = ChargedScheduleService.Project(schedule, Start, 15000.0, CapacityWh);

            Assert.Equal(CapacityWh, soc[Start]);
            Assert.Equal(0.0, soc[Start.AddMinutes(15)]);
        }

        [Fact]
        public void Quarters_before_the_starting_point_are_skipped()
        {
            var schedule = new Dictionary<DateTime, double>
            {
                [Start.AddMinutes(-15)] = -4000.0,
                [Start] = -4000.0,
            };

            var soc = ChargedScheduleService.Project(schedule, Start, 5000.0, CapacityWh);

            Assert.Single(soc);
            Assert.True(soc.ContainsKey(Start));
        }

        [Fact]
        public void Without_capacity_there_is_nothing_to_project()
        {
            var schedule = new Dictionary<DateTime, double> { [Start] = -4000.0 };

            Assert.Empty(ChargedScheduleService.Project(schedule, Start, 5000.0, capacityWh: 0.0));
        }
    }
}

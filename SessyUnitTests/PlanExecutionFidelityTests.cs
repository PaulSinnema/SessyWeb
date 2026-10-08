using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.StateMachine;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// v1.0.150: execution follows the plan. Zero Net Home covers the whole house at runtime, so the
    /// plan may only say ZNH where it covers the whole house; leftover energy goes to the house; at
    /// the reserve the runtime replans, guards hold the energy, and HoldReserve is entered at once.
    /// </summary>
    public class PlanExecutionFidelityTests
    {
        private const double CapacityKWh = 16.2;
        private const double ReserveKWh = 0.81;
        private static readonly DateTime Start = new(2026, 10, 6, 13, 0, 0);
        private static readonly DateTime PeakStart = Start.Date.AddHours(17).AddMinutes(45);
        private static readonly DateTime PeakEnd = Start.Date.AddHours(20).AddMinutes(15);
        private static readonly DischargeCapability Knee = new(PlateauW: 4669, KneeSoc: 0.30, Samples: 20);

        // 06-10 shape: cheap afternoon, 0.60 evening peak, no solar, 150 Wh house load per quarter.
        private static List<PricePoint> Points()
        {
            var points = new List<PricePoint>();
            for (var t = Start; t < Start.Date.AddDays(1); t = t.AddMinutes(15))
            {
                (double buy, double sell) =
                    t < Start.Date.AddHours(14).AddMinutes(15) ? (0.295, 0.251) :
                    t < Start.Date.AddHours(15) ? (0.313, 0.269) :
                    t < PeakStart ? (0.32, 0.276) :
                    t < PeakEnd ? (0.60, 0.556) :
                    (0.42, 0.376);

                points.Add(new PricePoint(t, buy, sell, NetLoadWh: 150.0, SolarSurplusWh: 0.0));
            }
            return points;
        }

        private static PlanResult Solve(double initialSocKWh, bool shift, DischargeCapability? capability)
        {
            var points = Points();
            var bounds = points.Select(p => new SocBound(p.Start, ReserveKWh, CapacityKWh)).ToList();
            var spec = new BatterySpec(CapacityKWh, initialSocKWh, MaxChargeKW: 6.6, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.87, DischargeEfficiency: 0.90, DischargeCapability: capability);
            var opt = new SessyOptions(QuarterMinutes: 15, CycleCostEurPerKWh: 0.001, AllowExport: true, AllowShift: shift);
            return BatteryGreedyPlanner.Solve(points, spec, opt, bounds)!;
        }

        private static double CapKWh(DischargeCapability? capability, double socKWh)
        {
            double capKW = 5.1;
            if (capability != null && capability.Samples > 0)
                capKW = Math.Min(capKW, capability.PowerW(socKWh / CapacityKWh) / 1000.0);
            return capKW * 0.25;
        }

        public static IEnumerable<object[]> Scenarios()
        {
            foreach (double soc in new[] { 0.0, 0.216, 0.467, 6.5, 12.0 })
                foreach (bool shift in new[] { true, false })
                    foreach (bool knee in new[] { true, false })
                        yield return new object[] { soc, shift, knee };
        }

        // ── Planner ───────────────────────────────────────────────────────────

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Zero_net_home_always_covers_the_whole_house(double soc, bool shift, bool knee)
        {
            var capability = knee ? Knee : null;
            var plan = Solve(soc, shift, capability);

            foreach (var step in plan.Plan.Where(p => p.Mode == ActionMode.ZeroNetHome))
            {
                double cover = Math.Min(0.150, CapKWh(capability, step.SocStartKWh));
                Assert.True(step.DischargeKW * 0.25 >= cover - 0.005,
                    $"{step.Start:HH:mm} ZNH plans {step.DischargeKW * 0.25:F3} of {cover:F3} kWh — runtime would cover all of it");
            }
        }

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void HoldReserve_never_discharges(double soc, bool shift, bool knee)
        {
            var plan = Solve(soc, shift, knee ? Knee : null);

            Assert.All(plan.Plan.Where(p => p.Mode == ActionMode.HoldReserve),
                p => Assert.Equal(0.0, p.DischargeKW, 6));
        }

        [Theory]
        [InlineData(6.5, true)]
        [InlineData(6.5, false)]
        [InlineData(12.0, true)]
        [InlineData(12.0, false)]
        public void Plan_never_ends_a_quarter_below_the_reserve(double soc, bool shift)
        {
            var plan = Solve(soc, shift, Knee);

            Assert.All(plan.Plan, p => Assert.True(p.SocEndKWh >= ReserveKWh - 0.01,
                $"{p.Start:HH:mm} {p.Mode} ends at {p.SocEndKWh:F3} kWh"));
        }

        [Fact]
        public void Leftover_energy_covers_the_house_instead_of_staying_in_the_battery()
        {
            // Before v1.0.150 this plan ended the day at 2,52 kWh while 21:15–23:45 imported at €0,42.
            var plan = Solve(0.467, shift: true, Knee);

            var evening = plan.Plan.Where(p => p.Start >= Start.Date.AddHours(20).AddMinutes(30)).ToList();
            Assert.True(evening.Count(p => p.Mode == ActionMode.ZeroNetHome) >= 8,
                "evening house load should be covered from the leftover energy");
            Assert.True(plan.Plan[^1].SocEndKWh <= ReserveKWh + 0.2,
                $"{plan.Plan[^1].SocEndKWh:F2} kWh left unused at the end of the horizon");
        }

        // ── Runtime ───────────────────────────────────────────────────────────

        private static readonly DateTime T0 = new(2026, 10, 7, 3, 0, 0);
        private static readonly TimeSpan Dwell = EnergySystemStateMachine.MinimumModeDwell;

        [Theory]
        [InlineData(Modes.ZeroNetHome)]
        [InlineData(Modes.Disabled)]
        [InlineData(Modes.Charging)]
        [InlineData(Modes.Discharging)]
        public void HoldReserve_is_entered_without_dwell(Modes current)
        {
            Assert.True(EnergySystemStateMachine.MayChangeMode(current, Modes.HoldReserve, T0, T0.AddSeconds(1), Dwell));
        }

        [Fact]
        public void Leaving_HoldReserve_still_waits_out_the_dwell()
        {
            Assert.False(EnergySystemStateMachine.MayChangeMode(Modes.HoldReserve, Modes.ZeroNetHome, T0, T0.AddSeconds(1), Dwell));
            Assert.True(EnergySystemStateMachine.MayChangeMode(Modes.HoldReserve, Modes.ZeroNetHome, T0, T0.Add(Dwell), Dwell));
        }

        [Theory]
        [InlineData(Modes.ZeroNetHome, 815.0, 810.0, true)]      // within 5 Wh of the floor
        [InlineData(Modes.ZeroNetHome, 500.0, 810.0, true)]      // below the floor
        [InlineData(Modes.ZeroNetHome, 816.0, 810.0, false)]     // still above
        [InlineData(Modes.HoldReserve, 500.0, 810.0, false)]       // already holding
        [InlineData(Modes.Discharging, 500.0, 810.0, false)]     // guard handles it
        [InlineData(Modes.ZeroNetHome, 0.0, 0.0, false)]         // no reserve to protect
        public void Reserve_rebuild_fires_only_for_ZNH_at_the_floor(Modes planned, double socWh, double floorWh, bool expected)
        {
            Assert.Equal(expected, MilpServiceBase.ReserveReachedUnderCover(planned, socWh, floorWh));
        }

        [Fact]
        public void Planner_floor_is_the_highest_reserve_still_to_come_within_the_horizon()
        {
            var now = new DateTime(2026, 10, 7, 21, 0, 0);
            var minSoc = new Dictionary<DateTime, double>
            {
                [now.AddMinutes(-15)] = 3000.0,        // past: ignored
                [now] = 810.0,
                [now.AddHours(2)] = 1500.0,            // bridge reserve later on
                [now.AddHours(30)] = 4000.0,           // beyond the horizon: ignored
            };

            Assert.Equal(1500.0, MilpServiceBase.PlannerFloorWh(minSoc, now, now.AddHours(24)));
            Assert.Equal(4000.0, MilpServiceBase.PlannerFloorWh(minSoc, now, DateTime.MaxValue));
        }

        [Fact]
        public void Guards_hold_the_energy()
        {
            var action = MilpServiceBase.HoldAction(null);

            Assert.Equal(Modes.HoldReserve, action.Mode);
            Assert.Equal(0.0, action.PowerW);
        }
    }
}

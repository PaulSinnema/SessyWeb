using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// The baseline no longer stores solar surplus unconditionally. It stores only when keeping the
    /// energy for a later quarter beats exporting it now; otherwise the battery stays idle
    /// (ActionMode.Disabled) and the surplus is exported. This pins that decision down, and the
    /// matching runtime rule in MilpServiceBase.SelectIdleMode.
    /// </summary>
    public class SolarExportVsStoreTests
    {
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;

        private static readonly DateTime Start = new(2026, 9, 2, 8, 0, 0);

        private static SessyOptions Options() => new(QuarterMinutes: 15, CycleCostEurPerKWh: 0.01);

        private static BatterySpec Spec() =>
            new(CapacityKWh, 0.0, MaxChargeKW, MaxDischargeKW,
                ChargeEfficiency: 0.95, DischargeEfficiency: 0.95);

        private static List<SocBound> Bounds(List<PricePoint> prices) =>
            prices.Select(p => new SocBound(p.Start, 0.0, CapacityKWh)).ToList();

        [Fact]
        public void Expensive_morning_surplus_is_exported_not_stored()
        {
            var prices = new List<PricePoint>
            {
                // Expensive morning peak with 2 kWh of solar surplus.
                new(Start, BuyEurPerKWh: 0.39, SellEurPerKWh: 0.35, NetLoadWh: -2000.0, SolarSurplusWh: 2000.0)
            };
            // The rest of the day is cheap and flat: nothing later is worth storing the morning
            // solar for, so exporting at 0.35 now beats keeping it.
            for (int i = 1; i < 8; i++)
                prices.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.10, SellEurPerKWh: 0.06, NetLoadWh: 0.0, SolarSurplusWh: 0.0));

            var result = BatteryGreedyPlanner.Solve(prices, Spec(), Options(), Bounds(prices));

            Assert.NotNull(result);
            var first = result!.Plan[0];

            Assert.Equal(ActionMode.Disabled, first.Mode);
            Assert.True(first.ChargeKW < 0.01,
                $"morning solar should not be stored, but ChargeKW was {first.ChargeKW:F2}");
        }

        [Fact]
        public void Cheap_morning_surplus_is_stored_for_an_expensive_evening()
        {
            var prices = new List<PricePoint>
            {
                // Cheap morning with 2 kWh of solar surplus.
                new(Start, BuyEurPerKWh: 0.09, SellEurPerKWh: 0.05, NetLoadWh: -2000.0, SolarSurplusWh: 2000.0)
            };
            // A later expensive deficit: storing the morning solar to avoid that import is clearly
            // worth more than exporting it now at 0.05.
            for (int i = 1; i < 4; i++)
                prices.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.09, SellEurPerKWh: 0.05, NetLoadWh: 0.0, SolarSurplusWh: 0.0));
            for (int i = 4; i < 8; i++)
                prices.Add(new PricePoint(Start.AddMinutes(15 * i),
                    BuyEurPerKWh: 0.40, SellEurPerKWh: 0.36, NetLoadWh: 1000.0, SolarSurplusWh: 0.0));

            var result = BatteryGreedyPlanner.Solve(prices, Spec(), Options(), Bounds(prices));

            Assert.NotNull(result);
            var first = result!.Plan[0];

            Assert.NotEqual(ActionMode.Disabled, first.Mode);
            Assert.True(first.ChargeKW > 1.0,
                $"cheap morning solar should be stored, but ChargeKW was {first.ChargeKW:F2}");
        }

        [Fact]
        public void SelectIdleMode_exports_the_surplus_when_the_plan_wants_it()
        {
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: -400.0, hasRoom: true, belowCycleCost: false,
                previous: Modes.ZeroNetHome, deadbandWh: 50.0, planWantsExport: true);

            Assert.Equal(Modes.Disabled, mode);
        }

        [Fact]
        public void SelectIdleMode_still_stores_the_surplus_by_default()
        {
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: -400.0, hasRoom: true, belowCycleCost: false,
                previous: Modes.ZeroNetHome, deadbandWh: 50.0, planWantsExport: false);

            Assert.Equal(Modes.ZeroNetHome, mode);
        }
    }
}

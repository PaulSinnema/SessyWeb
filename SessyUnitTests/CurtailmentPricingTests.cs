using Microsoft.Extensions.Logging.Abstractions;
using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.StateMachine;
using Xunit;
using static SessyController.Services.Items.ChargingModes;

namespace SessyTests.Services
{
    /// <summary>
    /// Curtailment is only ever triggered by EnergySystemInput.SellingPriceIsNegative — a single
    /// bool. EnergySystemStateMachineTests exercises the state machine against that bool directly.
    /// These tests instead build the selling price the way production does — market price, energy
    /// tax, return-delivery compensation and VAT, via EnergyPriceCalculator — for both netting=true
    /// and netting=false, and confirm the resulting sign drives the correct CurtailmentMode.
    ///
    /// The interesting case is netting: with netting, energy tax and VAT are added to the market
    /// price before the (negative) compensation is subtracted, so a mildly negative market price can
    /// still net out positive. Without netting there is no tax/VAT cushion, so the same market price
    /// can flip curtailment on. Several tests below use the identical market price under both netting
    /// states specifically to show that flip.
    /// </summary>
    public class CurtailmentPricingTests
    {
        // Realistic Dutch tariff components — same values as EnergyPriceCalculatorTests.
        private const double EnergyTax = 0.09157;    // energiebelasting
        private const double ReturnComp = -0.0182;   // verkoopvergoeding (stored negative)
        private const double Vat = 1.21;             // 21% BTW

        private const double Capacity = 16200.0;
        private const double FullSoc = Capacity * 0.996;  // above 99.5% "full" threshold
        private const double HalfSoc = Capacity * 0.5;

        private readonly EnergySystemStateMachine _sut = new(new NullLogger<EnergySystemStateMachine>());

        /// <summary>All-in selling price exactly as CalculationService derives it (see
        /// EnergyPriceCalculator's own doc comment for the two formulas).</summary>
        private static double SellingPrice(double marketPrice, bool netting) =>
            EnergyPriceCalculator.Calculate(
                marketPrice, overhead: 0.0, energyTax: EnergyTax, compensation: ReturnComp,
                vatFactor: Vat, buying: false, netting: netting);

        private static PriceDrivenInput Input(
            double sellingPriceEurPerKWh,
            double actualBatteryPowerW,
            double socWh,
            bool inverterAvailable,
            Modes plannedMode,
            double plannedSetpointW = 4000.0,
            double maxChargeSetpointW = 5000.0) =>
            new(sellingPriceEurPerKWh, actualBatteryPowerW, socWh, Capacity,
                inverterAvailable, plannedMode, plannedSetpointW, maxChargeSetpointW);

        /// <summary>
        /// Concrete test subclass of EnergySystemInput. Only the selling price and battery/hardware
        /// state are set directly — BatteryIsFull / BatteryIsActuallyCharging / SellingPriceIsNegative
        /// are the REAL base-class computations (BatteryConstants thresholds), not overridden, so
        /// these tests exercise the same threshold logic production uses.
        /// </summary>
        private class PriceDrivenInput : EnergySystemInput
        {
            public PriceDrivenInput(
                double sellingPriceEurPerKWh,
                double actualBatteryPowerW,
                double currentSocWh,
                double totalCapacityWh,
                bool inverterIsAvailable,
                Modes plannedMode,
                double plannedSetpointW,
                double maxChargeSetpointW) : base(null!, null!, null!, null!)
            {
                SellingPriceEurPerKWh = sellingPriceEurPerKWh;
                ActualBatteryPowerW = actualBatteryPowerW;
                CurrentSocWh = currentSocWh;
                TotalCapacityWh = totalCapacityWh;
                InverterIsAvailable = inverterIsAvailable;
                PlannedMode = plannedMode;
                PlannedSetpointW = plannedSetpointW;
                MaxChargeSetpointW = maxChargeSetpointW;
                IsLoaded = true;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Netting = true — tax/VAT cushion can keep a mildly negative market price
        // net-positive, so curtailment must NOT trigger.
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Netting_MildlyNegativeMarket_NetsPositive_NoCurtailment()
        {
            // market -0.05: (-0.05 + 0.09157 - 0.0182) * 1.21 ≈ +0.0283 — positive despite negative EPEX.
            double price = SellingPrice(-0.05, netting: true);
            Assert.True(price > 0.0, $"Expected net-positive selling price, got {price:F4}");

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, HalfSoc, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void Netting_DeeplyNegativeMarket_NetsNegative_BatteryCharging_ZeroExport()
        {
            // market -0.10: (-0.10 + 0.09157 - 0.0182) * 1.21 ≈ -0.0322 — negative even with netting.
            double price = SellingPrice(-0.10, netting: true);
            Assert.True(price < 0.0, $"Expected net-negative selling price, got {price:F4}");

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: -4000, HalfSoc, true, Modes.Charging, 4000));

            Assert.Equal(CurtailmentMode.ZeroExport, action.CurtailmentMode);
            Assert.Equal(Modes.Charging, action.BatteryMode);
            Assert.True(action.IsOverride);
        }

        [Fact]
        public void Netting_DeeplyNegativeMarket_NetsNegative_BatteryFull_Throttle()
        {
            double price = SellingPrice(-0.10, netting: true);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, FullSoc, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Throttle, action.CurtailmentMode);
            Assert.Equal(Modes.Disabled, action.BatteryMode);
        }

        [Fact]
        public void Netting_DeeplyNegativeMarket_NetsNegative_BatteryNotFullNotCharging_Shutdown()
        {
            double price = SellingPrice(-0.10, netting: true);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, HalfSoc, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Shutdown, action.CurtailmentMode);
            Assert.Equal(Modes.Charging, action.BatteryMode);
            Assert.Equal(5000.0, action.BatterySetpointW);
            Assert.Equal(0.0, action.InverterSetpointW);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Netting = false — no tax/VAT cushion, so the SAME mildly negative market
        // price that stayed positive with netting now flips curtailment on.
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void NoNetting_SameMildlyNegativeMarket_NetsNegative_UnlikeWithNetting()
        {
            // Same -0.05 market price as the netting=true test above, opposite outcome.
            double priceWithNetting = SellingPrice(-0.05, netting: true);
            double priceWithoutNetting = SellingPrice(-0.05, netting: false);

            Assert.True(priceWithNetting > 0.0, "Sanity check: netting case should stay positive.");
            Assert.True(priceWithoutNetting < 0.0, $"Expected net-negative without netting, got {priceWithoutNetting:F4}");
        }

        [Fact]
        public void NoNetting_SameMildlyNegativeMarket_BatteryCharging_ZeroExport()
        {
            double price = SellingPrice(-0.05, netting: false);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: -3000, HalfSoc, true, Modes.Charging, 3000));

            Assert.Equal(CurtailmentMode.ZeroExport, action.CurtailmentMode);
            Assert.True(action.IsOverride);
        }

        [Fact]
        public void NoNetting_SameMildlyNegativeMarket_BatteryFull_Throttle()
        {
            double price = SellingPrice(-0.05, netting: false);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, FullSoc, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Throttle, action.CurtailmentMode);
        }

        [Fact]
        public void NoNetting_SameMildlyNegativeMarket_NotFullNotCharging_Shutdown()
        {
            double price = SellingPrice(-0.05, netting: false);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, HalfSoc, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Shutdown, action.CurtailmentMode);
        }

        [Fact]
        public void NoNetting_PositiveMarket_NetsPositive_NoCurtailment()
        {
            // market +0.05: 0.05 - 0.0182 = +0.0318 — comfortably positive without any tax/VAT help.
            double price = SellingPrice(0.05, netting: false);
            Assert.True(price > 0.0);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, HalfSoc, true, Modes.Discharging, 3500));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.Equal(Modes.Discharging, action.BatteryMode);
            Assert.False(action.IsOverride);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Boundary: exactly zero is NOT negative (SellingPriceIsNegative uses "< 0.0").
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void SellingPriceExactlyZero_IsNotNegative_NoCurtailment()
        {
            var action = _sut.Evaluate(Input(0.0, actualBatteryPowerW: 0, HalfSoc, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Inverter offline during a genuine negative-price scenario still falls back
        // to the MILP plan, regardless of which netting regime produced the price.
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Netting_NegativePrice_InverterOffline_FallsBackToPlan()
        {
            double price = SellingPrice(-0.10, netting: true);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, HalfSoc, inverterAvailable: false, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void NoNetting_NegativePrice_InverterOffline_FallsBackToPlan()
        {
            double price = SellingPrice(-0.05, netting: false);

            var action = _sut.Evaluate(Input(price, actualBatteryPowerW: 0, HalfSoc, inverterAvailable: false, Modes.Discharging, 3000));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.Equal(Modes.Discharging, action.BatteryMode);
            Assert.False(action.IsOverride);
        }
    }
}
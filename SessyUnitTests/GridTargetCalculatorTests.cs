using SessyCommon.Enums;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Pins the P1 grid-target conversion. In NOM battery = houseNet - grid_target, so to reach a
    /// wanted power P the target must track the live house net load (consumption - solar).
    /// Signs: P1 net import +, export -; battery discharge +, charge -; grid target import +, export -.
    /// </summary>
    public class GridTargetCalculatorTests
    {
        private const double MaxChargeW = 6600;
        private const double MaxDischargeW = 5100;

        [Fact]
        public void HouseNet_is_p1_net_plus_battery()
        {
            // Grid imports 200 W while the battery discharges 800 W → house draws 1000 W.
            Assert.Equal(1000.0, GridTargetCalculator.HouseNetW(200, 800));
        }

        [Fact]
        public void Charging_adds_power_to_house_net()
        {
            // House draws 300 W, charge 2000 W → import 2300 W.
            var target = GridTargetCalculator.GridTargetW(Modes.Charging, 300, 2000, MaxChargeW, MaxDischargeW);
            Assert.Equal(2300, target);
        }

        [Fact]
        public void Discharging_subtracts_power_from_house_net()
        {
            // House draws 300 W, discharge 2000 W → export 1700 W (negative import).
            var target = GridTargetCalculator.GridTargetW(Modes.Discharging, 300, 2000, MaxChargeW, MaxDischargeW);
            Assert.Equal(-1700, target);
        }

        [Fact]
        public void ZeroNetHome_is_zero()
        {
            Assert.Equal(0, GridTargetCalculator.GridTargetW(Modes.ZeroNetHome, 1234, 5000, MaxChargeW, MaxDischargeW));
        }

        [Fact]
        public void Charge_power_is_clamped_to_nameplate()
        {
            var target = GridTargetCalculator.GridTargetW(Modes.Charging, 0, 999999, MaxChargeW, MaxDischargeW);
            Assert.Equal((int)MaxChargeW, target);
        }

        [Fact]
        public void Discharge_power_is_clamped_to_nameplate()
        {
            var target = GridTargetCalculator.GridTargetW(Modes.Discharging, 0, 999999, MaxChargeW, MaxDischargeW);
            Assert.Equal(-(int)MaxDischargeW, target);
        }

        [Fact]
        public void Negative_power_is_clamped_to_zero()
        {
            var target = GridTargetCalculator.GridTargetW(Modes.Charging, 500, -1000, MaxChargeW, MaxDischargeW);
            Assert.Equal(500, target);
        }

        [Fact]
        public void SafeClamp_bounds_implied_battery_to_nameplate()
        {
            // A wild import target: implied charge would be far past nameplate; clamp it back.
            var clamped = GridTargetCalculator.SafeClamp(999999, houseNetW: 300, MaxChargeW, MaxDischargeW);
            Assert.Equal(300 + (int)MaxChargeW, clamped);

            // A wild export target: implied discharge clamped to nameplate.
            var clampedExport = GridTargetCalculator.SafeClamp(-999999, houseNetW: 300, MaxChargeW, MaxDischargeW);
            Assert.Equal(300 - (int)MaxDischargeW, clampedExport);
        }
    }
}

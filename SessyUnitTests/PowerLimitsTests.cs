using System;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    public class PowerLimitsTests
    {
        [Fact]
        public void FallbackRatio_ZeroMeans80Percent()
        {
            Assert.Equal(0.80, PowerLimits.FallbackRatio(0.0), 9);
            Assert.Equal(0.65, PowerLimits.FallbackRatio(65.0), 9);
        }

        [Fact]
        public void BaseCaps_UseFallbackWithoutMeasuredModels()
        {
            Assert.Equal(6.6 * 0.8, PowerLimits.BaseChargeKW(6.6, ChargeTaper.None, 0.8), 9);
            Assert.Equal(5.1 * 0.8, PowerLimits.BaseDischargeKW(5.1, DischargeCapability.None, 0.8), 9);
        }

        [Fact]
        public void NoModels_ReturnCap()
        {
            double charge = PowerLimits.ChargeKWh(1.32, 0.5, 0.25, 6.6, ChargeCapability.None, ChargeTaper.None,
                ChargeCapabilityFloor.None, EfficiencyCurve.Flat(0.95, 0.95), 15.0, 15.0);
            double discharge = PowerLimits.DischargeKWh(1.02, 0.5, 0.25, DischargeCapability.None);

            Assert.Equal(1.32, charge, 9);
            Assert.Equal(1.02, discharge, 9);
        }
    }
}

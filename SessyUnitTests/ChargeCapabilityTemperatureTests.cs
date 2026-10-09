using System;
using System.Collections.Generic;
using System.Linq;
using SessyController.Services;
using SessyController.Services.Items;
using Xunit;

namespace SessyTests.Services
{
    public class ChargeCapabilityTemperatureTests
    {
        // Two SOC bins, power falls 50 W per °C, temperatures 14..30 °C.
        private static List<(double Soc, double PowerW, double? TemperatureC)> Samples(double slope = -50.0)
        {
            var list = new List<(double, double, double?)>();
            for (int i = 0; i < 20; i++)
            {
                double t = 14.0 + i * (16.0 / 19.0);
                list.Add((0.25, 5000.0 + slope * (t - 22.0), t));
                list.Add((0.65, 3000.0 + slope * (t - 22.0), t));
            }
            return list;
        }

        [Fact]
        public void Slope_IsFittedWithinBins()
        {
            var cap = ThrottleAnalysisService.FitChargeCapabilityWithTemperature(Samples());

            Assert.True(cap.HasTemperatureSlope);
            Assert.Equal(-50.0, cap.TemperatureSlopeWPerC, 6);
            Assert.Equal(14.0, cap.MinTemperatureC, 6);
            Assert.Equal(30.0, cap.MaxTemperatureC, 6);
        }

        [Fact]
        public void PowerW_CorrectsAndClampsToMeasuredRange()
        {
            var cap = ThrottleAnalysisService.FitChargeCapabilityWithTemperature(Samples());
            double atRef = cap.PowerW(0.25);

            Assert.Equal(atRef + 250.0, cap.PowerW(0.25, cap.BinTemperatureC![2] - 5.0), 6);
            // Colder than measured: no extrapolation past the coolest sample.
            Assert.Equal(cap.PowerW(0.25, 14.0), cap.PowerW(0.25, 0.0), 6);
        }

        [Fact]
        public void TooFewSamples_NoSlope()
        {
            var cap = ThrottleAnalysisService.FitChargeCapabilityWithTemperature(Samples().Take(20).ToList());

            Assert.False(cap.HasTemperatureSlope);
            Assert.Equal(cap.PowerW(0.25), cap.PowerW(0.25, 10.0), 6);
        }

        [Fact]
        public void NoTemperature_IsBaseFit()
        {
            var samples = Samples().Select(x => (x.Soc, x.PowerW, (double?)null)).ToList();
            var cap = ThrottleAnalysisService.FitChargeCapabilityWithTemperature(samples);

            Assert.False(cap.HasTemperatureSlope);
            Assert.True(cap.PowerW(0.25) > 0.0);
        }
    }
}

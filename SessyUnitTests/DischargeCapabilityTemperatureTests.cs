using System;
using System.Collections.Generic;
using System.Linq;
using SessyController.Services;
using SessyController.Services.Items;
using Xunit;

namespace SessyTests.Services
{
    public class DischargeCapabilityTemperatureTests
    {
        private static readonly DischargeCapability Base = new(PlateauW: 3600.0, KneeSoc: 0.2, Samples: 10);

        // Above the knee, power falls 45 W per °C, temperatures 10..30 °C.
        private static List<(double Soc, double PowerW, double? TemperatureC)> Samples()
        {
            var list = new List<(double, double, double?)>();
            for (int i = 0; i < 40; i++)
            {
                double t = 10.0 + i * 0.5;
                list.Add((i % 2 == 0 ? 0.45 : 0.75, 3600.0 - 45.0 * (t - 20.0), t));
            }
            list.Add((0.1, 1000.0, 5.0)); // below the knee: ignored
            return list;
        }

        [Fact]
        public void Slope_IsFittedAboveTheKnee()
        {
            var cap = ThrottleAnalysisService.WithDischargeTemperature(Base, Samples());

            Assert.True(cap.HasTemperatureSlope);
            Assert.Equal(-45.0, cap.TemperatureSlopeWPerC, 6);
            Assert.Equal(10.0, cap.MinTemperatureC, 6);
        }

        [Fact]
        public void Plateau_CorrectsClampsAndScalesTheKnee()
        {
            var cap = ThrottleAnalysisService.WithDischargeTemperature(Base, Samples());
            double reference = cap.ReferenceTemperatureC;

            Assert.Equal(3600.0 - 45.0 * 5.0, cap.PlateauAt(reference + 5.0), 6);
            Assert.Equal(cap.PlateauAt(10.0), cap.PlateauAt(-5.0), 6);
            Assert.Equal(cap.PowerW(0.1) * cap.PlateauAt(28.0) / 3600.0, cap.PowerW(0.1, 28.0), 6);
            Assert.Equal(cap.PowerW(0.5), cap.PowerW(0.5, double.NaN), 6);
        }

        [Fact]
        public void TooFewSamples_Unchanged()
        {
            var cap = ThrottleAnalysisService.WithDischargeTemperature(Base, Samples().Take(10).ToList());

            Assert.False(cap.HasTemperatureSlope);
            Assert.Equal(3600.0, cap.PlateauAt(30.0), 6);
        }
    }
}

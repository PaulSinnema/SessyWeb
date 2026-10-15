using SessyController.Services;
using SessyController.Services.Items;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>The knee follows full-request quarters only; without evidence the plateau runs to empty.</summary>
    public class DischargeMeasuredKneeTests
    {
        private static readonly DischargeCapability Envelope = new(PlateauW: 1622, KneeSoc: 0.6, Samples: 19);

        private static List<(double Soc, double PowerW)> Bin(double soc, double powerW, int count = 5)
            => Enumerable.Repeat((soc, powerW), count).ToList();

        [Fact]
        public void No_sustained_quarters_means_no_knee()
        {
            var result = ThrottleAnalysisService.WithMeasuredKnee(Envelope, []);

            Assert.Equal(0.0, result.KneeSoc);
            Assert.Equal(1622.0, result.PowerW(0.1), 6);
        }

        [Fact]
        public void Measured_fall_off_at_low_soc_sets_the_knee_above_it()
        {
            var sustained = Bin(0.05, 600).Concat(Bin(0.15, 1000)).Concat(Bin(0.25, 1600)).Concat(Bin(0.75, 1620)).ToList();

            var result = ThrottleAnalysisService.WithMeasuredKnee(Envelope, sustained);

            Assert.Equal(0.2, result.KneeSoc, 6);
        }

        [Fact]
        public void A_weak_high_bin_cannot_push_the_knee_up()
        {
            var sustained = Bin(0.25, 1600).Concat(Bin(0.95, 1200)).ToList();

            var result = ThrottleAnalysisService.WithMeasuredKnee(Envelope, sustained);

            Assert.Equal(0.0, result.KneeSoc);
        }
    }
}

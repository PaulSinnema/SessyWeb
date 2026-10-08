using SessyController.Services;
using SessyController.Services.Items;
using Xunit;

namespace SessyTests.Services
{
    public class ThrottleReportTests
    {
        private static readonly List<ThrottleSample> None = new();

        [Fact]
        public void Band_takes_the_median_and_its_share_of_nameplate()
        {
            var charge = new List<ThrottleSample> { new(0.05, 6600, 6000), new(0.10, 6600, 5000), new(0.15, 6600, 5500) };

            var r = ThrottleAnalysisService.BuildThrottleReport(charge, None, 6600, 5100, 16200);

            Assert.Equal(5, r.Bands.Count);
            Assert.Equal(3, r.Bands[0].ChargeQuarters);
            Assert.Equal(5500.0, r.Bands[0].ChargeMedianW!.Value, 6);
            Assert.Equal(5500.0 / 6600.0, r.Bands[0].ChargeShare!.Value, 6);
            Assert.Null(r.Bands[1].ChargeMedianW);
            Assert.Equal(16500.0 / 19800.0, r.Charge.Share!.Value, 6);
        }

        [Fact]
        public void Missing_band_leaves_the_full_pass_unknown()
        {
            var charge = new List<ThrottleSample> { new(0.5, 6600, 3300) };

            var r = ThrottleAnalysisService.BuildThrottleReport(charge, None, 6600, 5100, 16200);

            Assert.Null(r.Charge.FullCycleHours);
            Assert.Null(r.LossPerCycle(0.85).ChargeThrottleKWh);
        }

        [Fact]
        public void Full_soc_lands_in_the_top_band()
        {
            var discharge = new List<ThrottleSample> { new(1.0, 5100, 4000) };

            var r = ThrottleAnalysisService.BuildThrottleReport(None, discharge, 6600, 5100, 16200);

            Assert.Equal(1, r.Bands[4].DischargeQuarters);
            Assert.Equal("80-100%", r.Bands[4].Label);
        }

        [Fact]
        public void Loss_per_cycle_adds_efficiency_and_throttle()
        {
            // Charge at half nameplate in every band, discharge at full nameplate.
            var charge = Enumerable.Range(0, 5).Select(b => new ThrottleSample(b * 0.2 + 0.1, 6600, 3300)).ToList();
            var discharge = Enumerable.Range(0, 5).Select(b => new ThrottleSample(b * 0.2 + 0.1, 5100, 5100)).ToList();

            var r = ThrottleAnalysisService.BuildThrottleReport(charge, discharge, 6600, 5100, 16200);
            var loss = r.LossPerCycle(0.85);

            Assert.Equal(2.0 * 16200.0 / 6600.0, r.Charge.FullCycleHours!.Value, 6);
            Assert.Equal(0.5, r.Charge.CycleShare!.Value, 6);
            Assert.Equal(16.2 * 0.15, loss.EfficiencyKWh, 6);
            Assert.Equal(8.1, loss.ChargeThrottleKWh!.Value, 6);
            Assert.Equal(0.0, loss.DischargeThrottleKWh!.Value, 6);
            Assert.Equal(16.2 * 0.15 + 8.1, loss.TotalKWh, 6);
        }

        [Fact]
        public void Empty_report_has_no_data()
        {
            Assert.False(ThrottleReport.Empty.HasData);
            Assert.False(ThrottleAnalysisService.BuildThrottleReport(None, None, 6600, 5100, 16200).HasData);
        }
    }
}

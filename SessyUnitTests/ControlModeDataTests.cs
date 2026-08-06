using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Everything keeps being recorded in every control mode, but not every consumer may treat
    /// every quarter as evidence. These pin the two rules that follow from that.
    /// </summary>
    public class ControlModeDataTests
    {
        // ── Which quarters count as throttle evidence ─────────────────────────

        [Theory]
        [InlineData("Charged")]
        [InlineData("Provider")]
        [InlineData("Manual")]
        public void Quarters_driven_by_someone_else_are_not_throttle_evidence(string mode)
        {
            // The ratio divides our planned setpoint by the realized power. Under Charged that is
            // Charged's power over a request we never sent; under manual override we command full
            // power on clock hours rather than the planned setpoint. Neither says anything about
            // what the hardware would have delivered on our request.
            Assert.True(ThrottleAnalysisService.IsForeign(mode));
        }

        [Fact]
        public void Our_own_quarters_are_evidence()
        {
            Assert.False(ThrottleAnalysisService.IsForeign("SessyWeb"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Quarters_without_a_recorded_mode_still_count(string? mode)
        {
            // Rows written before v1.0.51 carry no mode. Dropping those would throw away the whole
            // existing history — and that history is the only evidence the taper fit has.
            Assert.False(ThrottleAnalysisService.IsForeign(mode));
        }

        // ── ENTSO-E fallback: hourly source onto a quarter-aligned planner ────

        [Fact]
        public void An_hourly_price_fills_four_quarters_at_the_same_price()
        {
            var start = new DateTime(2026, 8, 6, 12, 0, 0);

            var quarters = EPEXPricesService.ExpandToQuarters(new Dictionary<DateTime, double>
            {
                [start] = 0.30,
                [start.AddHours(1)] = 0.20
            });

            // Two hours in, eight quarters out — the last point holds for an hour of its own.
            Assert.Equal(8, quarters.Count);

            // A price is a rate per kWh, so it repeats. Dividing by four would make an hourly
            // source look four times cheaper than a quarterly one and the planner would buy on it.
            for (int i = 0; i < 4; i++)
                Assert.Equal(0.30, quarters[start.AddMinutes(15 * i)].Price);

            for (int i = 4; i < 8; i++)
                Assert.Equal(0.20, quarters[start.AddMinutes(15 * i)].Price);
        }

        [Fact]
        public void A_quarterly_series_comes_out_unchanged()
        {
            var start = new DateTime(2026, 8, 6, 12, 0, 0);

            var source = new Dictionary<DateTime, double>
            {
                [start] = 0.10,
                [start.AddMinutes(15)] = 0.11,
                [start.AddMinutes(30)] = 0.12
            };

            var quarters = EPEXPricesService.ExpandToQuarters(source);

            // The first two keep their own price; the last one holds for an hour (4 quarters).
            Assert.Equal(0.10, quarters[start].Price);
            Assert.Equal(0.11, quarters[start.AddMinutes(15)].Price);
            Assert.Equal(0.12, quarters[start.AddMinutes(30)].Price);
            Assert.Equal(6, quarters.Count);
        }

        [Fact]
        public void The_fallback_carries_no_planned_power()
        {
            var start = new DateTime(2026, 8, 6, 12, 0, 0);

            var quarters = EPEXPricesService.ExpandToQuarters(
                new Dictionary<DateTime, double> { [start] = 0.25 });

            // Sessy's own planned power does not exist in the ENTSO-E feed. 0 is honest;
            // inventing a number here would show up on the chart as a schedule that never was.
            Assert.All(quarters.Values, q => Assert.Equal(0.0, q.Power));
        }
    }
}

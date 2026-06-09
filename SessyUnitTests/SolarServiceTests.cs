using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    public class SolarServiceTests
    {
        private const double Alpha = 0.1;

        // ── EMA formula ───────────────────────────────────────────────────────

        [Fact]
        public void UpdateEma_SingleStep_IsWeightedCorrectly()
        {
            double ema = SolarService.UpdateEma(1.0, 0.48, Alpha);
            double expected = Alpha * 0.48 + (1.0 - Alpha) * 1.0; // = 0.948
            Assert.Equal(expected, ema, precision: 6);
        }

        [Fact]
        public void UpdateEma_ConvergesSlowlyToRawFactor()
        {
            // Starting at 1.0, raw = 0.48, alpha = 0.1.
            // After 10 steps: ~0.65
            double ema = 1.0;
            for (int i = 0; i < 10; i++)
                ema = SolarService.UpdateEma(ema, 0.48, Alpha);

            Assert.InRange(ema, 0.60, 0.70);
        }

        [Fact]
        public void UpdateEma_ConvergesFullyAfterManySteps()
        {
            double ema = 1.0;
            for (int i = 0; i < 1000; i++)
                ema = SolarService.UpdateEma(ema, 0.48, Alpha);

            Assert.InRange(ema, 0.47, 0.49);
        }

        [Fact]
        public void UpdateEma_AlreadyAtTarget_RemainsStable()
        {
            // EMA = raw → should not change.
            double ema = SolarService.UpdateEma(0.48, 0.48, Alpha);
            Assert.Equal(0.48, ema, precision: 6);
        }

        [Fact]
        public void UpdateEma_RisingRaw_EmaIncreasesGradually()
        {
            double ema = 0.5;
            // Apply 10 steps with raw = 1.5 (sunny day after cloudy period).
            for (int i = 0; i < 10; i++)
                ema = SolarService.UpdateEma(ema, 1.5, Alpha);

            Assert.True(ema > 0.5, "EMA should increase toward raw=1.5");
            Assert.True(ema < 1.5, "EMA should not yet reach raw after only 10 steps");
        }

        [Fact]
        public void UpdateEma_HigherAlpha_ConvergesFaster()
        {
            double emaLow = 1.0;
            double emaHigh = 1.0;
            double raw = 0.48;

            for (int i = 0; i < 10; i++)
            {
                emaLow = SolarService.UpdateEma(emaLow, raw, alpha: 0.1);
                emaHigh = SolarService.UpdateEma(emaHigh, raw, alpha: 0.5);
            }

            // Higher alpha should be closer to raw.
            Assert.True(emaHigh < emaLow, "Higher alpha should converge faster to raw");
        }

        [Fact]
        public void UpdateEma_RawClampedAt_0_2_WhenBelowMinimum()
        {
            // rawFactor = 0.0 should be clamped to 0.2 before EMA update.
            double rawClamped = Math.Max(0.2, Math.Min(3.0, 0.0));
            double ema = SolarService.UpdateEma(1.0, rawClamped, Alpha);
            Assert.InRange(ema, 0.91, 0.93); // 0.1 * 0.2 + 0.9 * 1.0 = 0.92
        }

        [Fact]
        public void UpdateEma_RawClampedAt_3_0_WhenAboveMaximum()
        {
            double rawClamped = Math.Max(0.2, Math.Min(3.0, 10.0));
            double ema = SolarService.UpdateEma(1.0, rawClamped, Alpha);
            // 0.1 * 3.0 + 0.9 * 1.0 = 1.20
            Assert.Equal(1.20, ema, precision: 6);
        }

        // ── Rate-limiting ─────────────────────────────────────────────────────

        [Fact]
        public void EmaRateLimit_MultipleCallsSameQuarter_OnlyFirstUpdates()
        {
            var lastUpdated = DateTime.MinValue;
            double ema = 1.0;
            var quarter = new DateTime(2026, 6, 9, 14, 0, 0);

            if (quarter > lastUpdated) { ema = SolarService.UpdateEma(ema, 0.48, Alpha); lastUpdated = quarter; }
            double afterFirst = ema;

            for (int i = 0; i < 10; i++)
                if (quarter > lastUpdated) { ema = SolarService.UpdateEma(ema, 0.48, Alpha); lastUpdated = quarter; }

            Assert.Equal(afterFirst, ema, precision: 10);
        }

        [Fact]
        public void EmaRateLimit_NextQuarter_AllowsUpdate()
        {
            var lastUpdated = new DateTime(2026, 6, 9, 14, 0, 0);
            double ema = 1.0;
            var nextQuarter = new DateTime(2026, 6, 9, 14, 15, 0);

            if (nextQuarter > lastUpdated) { ema = SolarService.UpdateEma(ema, 0.48, Alpha); lastUpdated = nextQuarter; }

            Assert.Equal(SolarService.UpdateEma(1.0, 0.48, Alpha), ema, precision: 10);
        }

        [Fact]
        public void EmaRateLimit_SixCyclesPerQuarter_SameAsOneCycle()
        {
            var quarter = new DateTime(2026, 6, 9, 14, 0, 0);
            var lastUpdated = DateTime.MinValue;
            double emaSix = 1.0;

            for (int i = 0; i < 6; i++)
                if (quarter > lastUpdated) { emaSix = SolarService.UpdateEma(emaSix, 0.48, Alpha); lastUpdated = quarter; }

            Assert.Equal(SolarService.UpdateEma(1.0, 0.48, Alpha), emaSix, precision: 10);
        }

        [Fact]
        public void EmaRateLimit_FourQuarters_YieldsFourSteps()
        {
            // Simulating 4 consecutive quarters with raw=0.48 should yield the same
            // result as applying UpdateEma 4 times sequentially.
            double raw = 0.48;
            var start = new DateTime(2026, 6, 9, 14, 0, 0);

            // Expected: 4 manual steps.
            double expected = 1.0;
            for (int i = 0; i < 4; i++)
                expected = SolarService.UpdateEma(expected, raw, Alpha);

            // Rate-limited simulation over 4 quarters, 3 cycles each.
            var lastUpdated = DateTime.MinValue;
            double ema = 1.0;
            for (int q = 0; q < 4; q++)
            {
                var quarter = start.AddMinutes(q * 15);
                for (int cycle = 0; cycle < 3; cycle++)
                    if (quarter > lastUpdated) { ema = SolarService.UpdateEma(ema, raw, Alpha); lastUpdated = quarter; }
            }

            Assert.Equal(expected, ema, precision: 10);
        }

        [Fact]
        public void EmaRateLimit_DayBoundary_AllowsFirstQuarterOfNewDay()
        {
            var lastUpdated = new DateTime(2026, 6, 9, 23, 45, 0);
            double ema = 0.8;
            var firstNextDay = new DateTime(2026, 6, 10, 0, 0, 0);

            if (firstNextDay > lastUpdated) { ema = SolarService.UpdateEma(ema, 1.0, Alpha); lastUpdated = firstNextDay; }

            double expected = SolarService.UpdateEma(0.8, 1.0, Alpha);
            Assert.Equal(expected, ema, precision: 10);
        }
    }
}
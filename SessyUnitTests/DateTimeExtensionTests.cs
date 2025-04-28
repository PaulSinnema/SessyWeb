using SessyCommon.Extensions;
using static SessyCommon.Extensions.DateTimeExtension;

namespace SessyUnitTests
{
    public class DateTimeQuarterHourExtensionTests
    {
        [Theory]
        // Already on a boundary
        [InlineData("2025-04-28T10:00:00", "2025-04-28T10:00:00")]
        // Just below half‑way (should round down)
        [InlineData("2025-04-28T10:07:29", "2025-04-28T10:00:00")]
        // Exactly half‑way (should round up)
        [InlineData("2025-04-28T10:07:30", "2025-04-28T10:15:00")]
        // Between 15 and 30 boundary but closer to 15 (round down)
        [InlineData("2025-04-28T10:22:00", "2025-04-28T10:15:00")]
        // Between 30 and 45 boundary but closer to 45 (round up)
        [InlineData("2025-04-28T10:37:31", "2025-04-28T10:45:00")]
        // Edge case at the end of the day
        [InlineData("2025-04-28T23:59:00", "2025-04-29T00:00:00")]
        public void RoundToNearestQuarterHour_ReturnsExpected(string input, string expected)
        {
            // Arrange
            var inputDt = DateTime.Parse(input, null, System.Globalization.DateTimeStyles.RoundtripKind);
            var expectedDt = DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind);

            // Act
            var actual = inputDt.DateNearestQuarter();

            // Assert
            Assert.Equal(expectedDt, actual);
            Assert.Equal(inputDt.Kind, actual.Kind); // Kind should be preserved
        }


        [Theory]
        // On boundary
        [InlineData("2025-04-28T10:00:00", "2025-04-28T10:00:00")]
        // Any value before 10:15 should floor to 10:00
        [InlineData("2025-04-28T10:07:29", "2025-04-28T10:00:00")]
        [InlineData("2025-04-28T10:07:30", "2025-04-28T10:00:00")]
        // Between 15 and 30 → 15
        [InlineData("2025-04-28T10:22:00", "2025-04-28T10:15:00")]
        // Between 30 and 45 → 30
        [InlineData("2025-04-28T10:37:31", "2025-04-28T10:30:00")]
        // Final minute of the hour → 45
        [InlineData("2025-04-28T10:59:00", "2025-04-28T10:45:00")]
        // Crossing midnight remains same date but floors to previous quarter
        [InlineData("2025-04-28T23:59:00", "2025-04-28T23:45:00")]
        public void FloorToQuarterHour_ReturnsExpected(string input, string expected)
        {
            // Arrange
            var inputDt = DateTime.Parse(input, null, System.Globalization.DateTimeStyles.RoundtripKind);
            var expectedDt = DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind);

            // Act
            var actual = inputDt.DateFloorQuarter();

            // Assert
            Assert.Equal(expectedDt, actual);
            Assert.Equal(inputDt.Kind, actual.Kind);
        }

        [Fact]
        public void Detects_SixtyMinute_Resolution()
        {
            var series = new[]
            {
                new DateTime(2025, 4, 28, 10, 0, 0),
                new DateTime(2025, 4, 28, 11, 0, 0),
                new DateTime(2025, 4, 28, 12, 0, 0)
            };

            Assert.Equal(TimeResolution.SixtyMinutes, series.GetTimeResolution());
        }

        [Fact]
        public void Detects_FifteenMinute_Resolution()
        {
            var series = new[]
            {
                new DateTime(2025, 4, 28, 10,  0, 0),
                new DateTime(2025, 4, 28, 10, 15, 0),
                new DateTime(2025, 4, 28, 10, 30, 0),
                new DateTime(2025, 4, 28, 10, 45, 0),
                new DateTime(2025, 4, 28, 11,  0, 0)
            };

            Assert.Equal(TimeResolution.FifteenMinutes, series.GetTimeResolution());
        }

        [Fact]
        public void Mixed_Intervals_Return_Unknown()
        {
            var series = new[]
            {
                new DateTime(2025, 4, 28, 10, 0, 0),
                new DateTime(2025, 4, 28, 10, 15, 0),
                new DateTime(2025, 4, 28, 11, 15, 0)   // 60 min + 15 min ⇒ gemengd
            };

            Assert.Equal(TimeResolution.Unknown, series.GetTimeResolution());
        }

        [Fact]
        public void Duplicate_TimePoints_Return_Unknown()
        {
            var series = new[]
            {
                new DateTime(2025, 4, 28, 10, 0, 0),
                new DateTime(2025, 4, 28, 10, 0, 0),   // delta = 0
                new DateTime(2025, 4, 28, 10, 15, 0)
            };

            Assert.Equal(TimeResolution.Unknown, series.GetTimeResolution());
        }

        [Fact]
        public void Single_Element_Returns_Unknown()
        {
            var series = new[]
            {
                new DateTime(2025, 4, 28, 10, 0, 0)
            };

            Assert.Equal(TimeResolution.Unknown, series.GetTimeResolution());
        }

        [Fact]
        public void Unsorted_Input_Still_Works()
        {
            var series = new[]
            {
                new DateTime(2025, 4, 28, 10, 30, 0),
                new DateTime(2025, 4, 28, 10,  0, 0),
                new DateTime(2025, 4, 28, 10, 45, 0),
                new DateTime(2025, 4, 28, 10, 15, 0)
            };

            Assert.Equal(TimeResolution.FifteenMinutes, series.GetTimeResolution());
        }
    }
}
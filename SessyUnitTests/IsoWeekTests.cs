using SessyCommon.Extensions;
using Xunit;

namespace SessyTests.Services
{
    public class IsoWeekTests
    {
        [Theory]
        [InlineData(2026, 11, 9)]   // Monday
        [InlineData(2026, 11, 12)]  // Thursday
        [InlineData(2026, 11, 15)]  // Sunday
        public void Week_runs_from_Monday_to_Sunday(int year, int month, int day)
        {
            var date = new DateTime(year, month, day, 14, 30, 0);

            Assert.Equal(new DateTime(2026, 11, 9), date.StartOfIsoWeek());
            Assert.Equal(new DateTime(2026, 11, 15), date.EndOfIsoWeek());
        }
    }
}

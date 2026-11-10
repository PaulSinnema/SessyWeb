using Newtonsoft.Json;
using SessyController.Services;
using Xunit;
using static SessyController.Services.Sessy;

namespace SessyTests.Services
{
    /// <summary>
    /// An unknown or missing system_state must not break PowerStatus: it carries the SOC.
    /// </summary>
    public class SystemStateParsingTests
    {
        private static PowerStatus? Parse(string systemStateJson)
            => JsonConvert.DeserializeObject<PowerStatus>(
                $"{{\"status\":\"ok\",\"sessy\":{{\"state_of_charge\":0.5,\"system_state\":{systemStateJson}}}}}");

        [Fact]
        public void Known_state_parses()
        {
            var ps = Parse("\"SYSTEM_STATE_STANDBY\"");

            Assert.Equal(SystemStates.SYSTEM_STATE_STANDBY, ps!.Sessy!.SystemState);
            Assert.Equal("Standby", ps.Sessy.SystemStateTitle);
        }

        [Theory]
        [InlineData("\"SYSTEM_STATE_SOMETHING_NEW\"")]
        [InlineData("null")]
        [InlineData("\"\"")]
        [InlineData("\"99\"")]
        public void Unknown_state_keeps_the_rest_of_the_status(string systemStateJson)
        {
            var ps = Parse(systemStateJson);

            Assert.Equal(0.5, ps!.Sessy!.StateOfCharge, 6);
            Assert.Equal(SystemStates.SYSTEM_STATE_UNKNOWN, ps.Sessy.SystemState);
            Assert.Equal("gray", ps.Sessy.SystemStateColor);
            Assert.Equal("Unknown", ps.Sessy.SystemStateTitle);
        }
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Managers;
using SessyController.Services;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// A household without solar panels, and more generally a config that leaves a whole section out.
    ///
    /// This was unreachable until v1.0.78: the appsettings.json baked into the image was loaded as
    /// a second configuration source, and .NET merges per key, so every section the user did not
    /// write was silently filled in from the template. With that template gone, PowerSystems really
    /// is absent, and SolarInverterManager dereferenced Endpoints in its constructor — the host
    /// failed to start with a NullReferenceException before a single service ran.
    /// </summary>
    public class NoSolarConfigurationTests
    {
        private static SolarInverterManager NewManager(
            PowerSystemsConfig config, params ISolarInverterService[] inverterServices)
        {
            var settingsService = new Mock<SettingsService>(
                null!, null!, null!, null!, Options.Create(new SettingsConfig()));

            settingsService.Setup(s => s.Current).Returns(new SessyData.Model.Settings());

            var monitor = new Mock<IOptionsMonitor<PowerSystemsConfig>>();
            monitor.Setup(m => m.CurrentValue).Returns(config);

            return new SolarInverterManager(
                inverterServices,
                new LoggingService<SolarInverterManager>(new Mock<ILogger<SolarInverterManager>>().Object),
                null!,
                settingsService.Object,
                monitor.Object);
        }

        private static ISolarInverterService Inverter(string providerName)
        {
            var mock = new Mock<ISolarInverterService>();

            mock.Setup(s => s.ProviderName).Returns(providerName);
            mock.Setup(s => s.Endpoints).Returns(new Dictionary<string, Endpoint>());

            return mock.Object;
        }

        // ── Configuration defaults ────────────────────────────────────────────

        [Fact]
        public void An_absent_section_binds_to_an_empty_dictionary_not_null()
        {
            Assert.NotNull(new PowerSystemsConfig().Endpoints);
            Assert.NotNull(new SessyP1Config().Endpoints);
            Assert.NotNull(new SessyBatteryConfig().Batteries);
        }

        [Fact]
        public void Totals_over_an_empty_battery_section_are_zero()
        {
            var config = new SessyBatteryConfig();

            Assert.Equal(0, config.TotalCapacity);
            Assert.Equal(0, config.TotalChargingCapacity);
            Assert.Equal(0, config.TotalDischargingCapacity);
        }

        // ── Credentials do not declare a device ───────────────────────────────
        // secrets.json is merged per key on top of appsettings.json, so credentials left behind for
        // a battery that was removed from appsettings.json used to conjure up a battery with no
        // address: "Could not get power status after 3 tries for battery 2", caused by
        // ArgumentNullException (Parameter 'BaseUrl') on every poll.

        [Fact]
        public void An_entry_without_a_base_url_is_not_a_device()
        {
            Assert.False(new SessyBatteryEndpoint { UserId = "u", Password = "p" }.IsConfigured);
            Assert.False(new SessyBatteryEndpoint { BaseUrl = "   " }.IsConfigured);
            Assert.True(new SessyBatteryEndpoint { BaseUrl = "http://192.168.1.241" }.IsConfigured);

            Assert.False(new SessyP1Endpoint { UserId = "u" }.IsConfigured);
            Assert.True(new SessyP1Endpoint { BaseUrl = "http://192.168.1.240" }.IsConfigured);
        }

        [Fact]
        public void Credentials_left_over_in_secrets_add_no_capacity()
        {
            var config = new SessyBatteryConfig
            {
                Batteries = new Dictionary<string, SessyBatteryEndpoint>
                {
                    ["1"] = new() { BaseUrl = "http://192.168.1.241", Capacity = 5400, MaxCharge = 2200, MaxDischarge = 1700 },
                    ["2"] = new() { UserId = "u", Password = "p" },
                    ["3"] = new() { UserId = "u", Password = "p" }
                }
            };

            Assert.Single(config.ConfiguredBatteries);
            Assert.Equal(5400, config.TotalCapacity);
            Assert.Equal(2200, config.TotalRawChargingCapacity);
            Assert.Equal(1700, config.TotalRawDischargingCapacity);
        }

        // ── The capability the UI hides on ────────────────────────────────────

        [Fact]
        public void HasSolar_needs_a_configured_inverter()
        {
            Assert.False(new PowerSystemsConfig().HasSolar);

            // A provider key with no inverters under it is not solar either.
            Assert.False(new PowerSystemsConfig
            {
                Endpoints = new Dictionary<string, Dictionary<string, Endpoint>> { ["SolarEdge"] = new() }
            }.HasSolar);

            Assert.True(new PowerSystemsConfig
            {
                Endpoints = new Dictionary<string, Dictionary<string, Endpoint>>
                {
                    ["SolarEdge"] = new() { ["1"] = new Endpoint { InverterMaxCapacity = 5000 } }
                }
            }.HasSolar);
        }

        // ── The startup crash ─────────────────────────────────────────────────

        [Fact]
        public void No_PowerSystems_section_activates_no_inverters()
        {
            var manager = NewManager(new PowerSystemsConfig(), Inverter("SolarEdge"));

            Assert.Empty(manager.ActiveInverterServices);
            Assert.Equal(0.0, manager.TotalCapacity);
            Assert.False(manager.IsAvailable);
        }

        [Fact]
        public void A_configured_provider_is_still_activated()
        {
            var config = new PowerSystemsConfig
            {
                Endpoints = new Dictionary<string, Dictionary<string, Endpoint>>
                {
                    ["SolarEdge"] = new() { ["1"] = new Endpoint { InverterMaxCapacity = 5000 } }
                }
            };

            var manager = NewManager(config, Inverter("SolarEdge"), Inverter("Enphase"));

            Assert.Single(manager.ActiveInverterServices);
            Assert.Equal("SolarEdge", manager.ActiveInverterServices[0].ProviderName);
        }

        // ── Consequences downstream ───────────────────────────────────────────

        [Fact]
        public async Task Throttling_without_inverters_is_a_no_op_not_an_error()
        {
            var manager = NewManager(new PowerSystemsConfig());

            // Used to throw "InverterMaxCapacity not set or wrong in config", which reads as a
            // broken configuration while the house simply has no panels.
            await manager.ThrottleInverterToWatts(0.0);

            Assert.Null(manager.LastSetpointW);
        }

        [Fact]
        public async Task Solar_power_without_inverters_is_zero()
        {
            var manager = NewManager(new PowerSystemsConfig());

            Assert.Equal(0.0, await manager.GetActualSolarPowerInWatts());
        }
    }
}

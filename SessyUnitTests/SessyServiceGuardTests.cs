using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Which requests actually leave SessyService, per control mode.
    ///
    /// The guards used to read Settings.WeAreInControl, which counted manual override as "someone
    /// else is in control". Every setpoint and strategy write issued under manual override was
    /// therefore dropped without a trace — the batteries simply kept their last command. These
    /// tests count the requests that reach the wire, so a guard that silently swallows one fails.
    /// </summary>
    public class SessyServiceGuardTests
    {
        private const string BatteryId = "Battery1";

        /// <summary>Counts requests and answers every one of them with 200 OK.</summary>
        private sealed class CountingHandler : HttpMessageHandler
        {
            public List<string> Paths { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });
            }
        }

        private static (SessyService Service, CountingHandler Handler) NewService(
            bool charged = false, bool manual = false, bool supplier = false)
        {
            var handler = new CountingHandler();

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>()))
                   .Returns(() => new HttpClient(handler, disposeHandler: false));

            var config = new SessyBatteryConfig
            {
                Batteries = new Dictionary<string, SessyBatteryEndpoint>
                {
                    [BatteryId] = new()
                    {
                        BaseUrl = "http://192.0.2.1",
                        UserId = "user",
                        Password = "password"
                    }
                }
            };

            // IOptionsMonitor, not IOptions: the service re-reads it so a config edit lands without
            // a restart. A fixed value back is all a test needs.
            var batteryConfig = Mock.Of<IOptionsMonitor<SessyBatteryConfig>>(m => m.CurrentValue == config);

            var settings = new Settings { ChargedInControl = charged, ManualOverride = manual };

            var settingsService = new Mock<SettingsService>(
                null!, null!, null!, null!, Options.Create(new SettingsConfig()));
            settingsService.Setup(s => s.Current).Returns(settings);

            var controlMode = new ControlModeService(
                new LoggingService<ControlModeService>(new Mock<ILogger<ControlModeService>>().Object),
                settingsService.Object);

            controlMode.Update(supplier);

            var service = new SessyService(
                new LoggingService<SessyService>(new Mock<ILogger<SessyService>>().Object),
                factory.Object,
                batteryConfig,
                new TimeZoneService(Options.Create(new SettingsConfig { Timezone = "Europe/Amsterdam" })),
                controlMode);

            return (service, handler);
        }

        // ── Writing ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Under_manual_override_a_setpoint_reaches_the_battery()
        {
            // The regression. Manual override is SessyWeb driving; blocking it here meant the
            // manual hour lists did nothing at all while the UI showed them as active.
            var (service, handler) = NewService(manual: true);

            await service.SetPowerSetpointAsync(BatteryId, new PowerSetpoint { Setpoint = -3000 });

            Assert.Contains("/api/v1/power/setpoint", handler.Paths);
        }

        [Fact]
        public async Task Under_manual_override_a_strategy_change_reaches_the_battery()
        {
            var (service, handler) = NewService(manual: true);

            await service.SetActivePowerStrategyAsync(
                BatteryId, new ActivePowerStrategy { Strategy = "POWER_STRATEGY_API" });

            Assert.Contains("/api/v1/power/active_strategy", handler.Paths);
        }

        [Fact]
        public async Task Under_our_own_control_a_setpoint_reaches_the_battery()
        {
            var (service, handler) = NewService();

            await service.SetPowerSetpointAsync(BatteryId, new PowerSetpoint { Setpoint = 2000 });

            Assert.Contains("/api/v1/power/setpoint", handler.Paths);
        }

        [Fact]
        public async Task Under_charged_nothing_is_written()
        {
            var (service, handler) = NewService(charged: true);

            await service.SetPowerSetpointAsync(BatteryId, new PowerSetpoint { Setpoint = 2000 });
            await service.SetActivePowerStrategyAsync(
                BatteryId, new ActivePowerStrategy { Strategy = "POWER_STRATEGY_API" });

            Assert.Empty(handler.Paths);
        }

        [Fact]
        public async Task Under_the_supplier_nothing_is_written()
        {
            var (service, handler) = NewService(supplier: true);

            await service.SetPowerSetpointAsync(BatteryId, new PowerSetpoint { Setpoint = 2000 });

            Assert.Empty(handler.Paths);
        }

        // ── Reading ───────────────────────────────────────────────────────────

        [Fact]
        public async Task The_strategy_is_readable_in_every_mode()
        {
            // What started this: with Charged in control the UI showed "Unknown" instead of the
            // strategy set in the Sessy portal, because a read sat behind a control guard.
            foreach (var (charged, manual, supplier) in new[]
                     {
                         (false, false, false),
                         (true, false, false),
                         (false, true, false),
                         (false, false, true)
                     })
            {
                var (service, handler) = NewService(charged, manual, supplier);

                await service.GetActivePowerStrategyAsync(BatteryId);

                Assert.Contains("/api/v1/power/active_strategy", handler.Paths);
            }
        }

        [Fact]
        public async Task The_schedule_is_readable_in_every_mode()
        {
            // The batteries keep planning for themselves whoever executes, and the day-ahead prices
            // are read from this same response — so it must never be gated on the control mode.
            foreach (var (charged, manual, supplier) in new[]
                     {
                         (false, false, false),
                         (true, false, false),
                         (false, true, false),
                         (false, false, true)
                     })
            {
                var (service, handler) = NewService(charged, manual, supplier);

                await service.GetScheduleAsync(BatteryId);

                Assert.Contains("/api/v2/dynamic/schedule", handler.Paths);
            }
        }
    }
}

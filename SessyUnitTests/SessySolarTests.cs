using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Managers;
using SessyController.Services;
using SessyController.Services.InverterServices;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Solar measured through the Sessy batteries instead of an inverter.
    ///
    /// Every Sessy reports renewable_energy_phase1/2/3 from CT clamps around the PV group. Households
    /// whose inverter SessyWeb cannot read had no solar term at all, and since consumption is
    /// solar + grid + battery, every quarter with net export came out negative and was discarded —
    /// issue #4.
    ///
    /// Two rules carry the risk and are tested here: the two sources are mutually exclusive (they
    /// measure the same panels), and the Sessy cannot curtail (it measures production, it cannot
    /// reduce it).
    /// </summary>
    public class SessySolarTests
    {
        private static SolarInverterManager NewManager(
            PowerSystemsConfig config, params ISolarInverterService[] inverterServices) =>
            NewManager(config, null, inverterServices);

        private static SolarInverterManager NewManager(
            PowerSystemsConfig config,
            SolCalc.Data.SunlightLevel? sunlight,
            params ISolarInverterService[] inverterServices)
        {
            var settingsService = new Mock<SettingsService>(null!, null!, null!, null!);

            settingsService.Setup(s => s.Current).Returns(new SessyData.Model.Settings());

            var monitor = new Mock<IOptionsMonitor<PowerSystemsConfig>>();
            monitor.Setup(m => m.CurrentValue).Returns(config);

            TimeZoneService? timeZoneService = null;

            if (sunlight.HasValue)
            {
                var clock = new Mock<TimeZoneService>();

                clock.Setup(t => t.GetSunlightLevel(It.IsAny<double>(), It.IsAny<double>())).Returns(sunlight.Value);
                clock.Setup(t => t.Now).Returns(new DateTime(2026, 8, 11, 12, 0, 0));

                timeZoneService = clock.Object;
            }

            return new SolarInverterManager(
                inverterServices,
                new LoggingService<SolarInverterManager>(new Mock<ILogger<SolarInverterManager>>().Object),
                timeZoneService!,
                settingsService.Object,
                monitor.Object);
        }

        private static Mock<ISolarInverterService> Inverter(string providerName,
                                                            bool supportsCurtailment,
                                                            double capacityW = 5000.0)
        {
            var mock = new Mock<ISolarInverterService>();

            mock.Setup(s => s.ProviderName).Returns(providerName);
            mock.Setup(s => s.SupportsCurtailment).Returns(supportsCurtailment);
            mock.Setup(s => s.IsAvailable).Returns(true);
            mock.Setup(s => s.TotalCapacity).Returns(capacityW);
            mock.Setup(s => s.Endpoints).Returns(new Dictionary<string, Endpoint>
            {
                { "1", new Endpoint { InverterMaxCapacity = capacityW } }
            });

            return mock;
        }

        private static PowerSystemsConfig ConfigFor(params string[] providerNames)
        {
            var config = new PowerSystemsConfig();

            foreach (var name in providerNames)
            {
                config.Endpoints[name] = new Dictionary<string, Endpoint>
                {
                    { "1", new Endpoint { InverterMaxCapacity = 5000.0 } }
                };
            }

            return config;
        }

        // ── Combining the phases ──────────────────────────────────────────────

        [Fact]
        public void Renewable_power_is_the_sum_of_the_three_phases()
        {
            var status = new PowerStatus
            {
                RenewableEnergyPhase1 = new Phase { Power = 1200 },
                RenewableEnergyPhase2 = new Phase { Power = 900 },
                RenewableEnergyPhase3 = new Phase { Power = 400 }
            };

            Assert.Equal(2500.0, SessyInverterService.SumRenewablePhases(status));
        }

        /// <summary>
        /// The whole reason summing over batteries is safe: measured on the production system, a
        /// Sessy without CT clamps reports 0 mA and 0 W on all three phases, so it adds nothing.
        /// </summary>
        [Fact]
        public void A_battery_without_clamps_contributes_nothing()
        {
            var withoutClamps = new PowerStatus
            {
                RenewableEnergyPhase1 = new Phase { Power = 0 },
                RenewableEnergyPhase2 = new Phase { Power = 0 },
                RenewableEnergyPhase3 = new Phase { Power = 0 }
            };

            Assert.Equal(0.0, SessyInverterService.SumRenewablePhases(withoutClamps));
        }

        [Fact]
        public void A_missing_phase_counts_as_zero_rather_than_throwing()
        {
            var status = new PowerStatus { RenewableEnergyPhase2 = new Phase { Power = 700 } };

            Assert.Equal(700.0, SessyInverterService.SumRenewablePhases(status));
        }

        // ── The capacity cap ──────────────────────────────────────────────────

        [Fact]
        public void A_reading_above_the_array_capacity_is_clamped_and_flagged()
        {
            var result = SessyInverterService.ClampToCapacity(14400.0, 5000.0, out bool exceeded);

            Assert.Equal(5000.0, result);
            Assert.True(exceeded);
        }

        /// <summary>
        /// The cap proves double counting one way only. Three batteries each reporting a real 1500 W
        /// add up to 4500 W and stay under a 5000 W cap, so passing it is not evidence of anything —
        /// which is why the cap clamps and reports instead of being used to pick a combining rule.
        /// </summary>
        [Fact]
        public void Double_counting_below_the_cap_is_not_detected()
        {
            var result = SessyInverterService.ClampToCapacity(4500.0, 5000.0, out bool exceeded);

            Assert.Equal(4500.0, result);
            Assert.False(exceeded);
        }

        [Fact]
        public void Without_a_configured_capacity_nothing_is_clamped()
        {
            var result = SessyInverterService.ClampToCapacity(14400.0, 0.0, out bool exceeded);

            Assert.Equal(14400.0, result);
            Assert.False(exceeded);
        }

        // ── Which batteries are read ──────────────────────────────────────────

        /// <summary>
        /// No filter means every battery, which is not the same as selecting none — a null selection
        /// says "take them all", an empty set says "take nothing".
        /// </summary>
        [Fact]
        public void Without_a_filter_every_battery_is_read()
        {
            var unknown = SessyInverterService.SelectBatteryIds(
                new[] { "1", "2", "3" }, null, out var selected);

            Assert.Null(selected);
            Assert.Empty(unknown);
        }

        [Fact]
        public void An_empty_filter_also_means_every_battery()
        {
            var unknown = SessyInverterService.SelectBatteryIds(
                new[] { "1", "2", "3" }, new List<string>(), out var selected);

            Assert.Null(selected);
            Assert.Empty(unknown);
        }

        [Fact]
        public void A_filter_narrows_the_selection_to_the_named_batteries()
        {
            var unknown = SessyInverterService.SelectBatteryIds(
                new[] { "1", "2", "3" }, new[] { "1" }, out var selected);

            Assert.NotNull(selected);
            Assert.Equal(new[] { "1" }, selected!.OrderBy(s => s));
            Assert.Empty(unknown);
        }

        [Fact]
        public void A_battery_that_does_not_exist_is_reported_by_name()
        {
            var unknown = SessyInverterService.SelectBatteryIds(
                new[] { "1", "2" }, new[] { "1", "4" }, out var selected);

            Assert.Equal(new[] { "1" }, selected!.OrderBy(s => s));
            Assert.Equal(new[] { "4" }, unknown);
        }

        /// <summary>
        /// A filter that matches nothing selects nothing. Falling back to every battery would ignore
        /// the reason the subset was written down, and the empty selection makes the source report
        /// itself unavailable rather than 0 W — which would be stored as a real measurement.
        /// </summary>
        [Fact]
        public void A_filter_matching_nothing_selects_nothing()
        {
            var unknown = SessyInverterService.SelectBatteryIds(
                new[] { "1", "2" }, new[] { "9" }, out var selected);

            Assert.NotNull(selected);
            Assert.Empty(selected!);
            Assert.Equal(new[] { "9" }, unknown);
        }

        /// <summary>
        /// Nothing is judged after sunset. Every source stops reading when the sun is down, so the
        /// timestamp the health check looks at goes stale on its own — without this the source was
        /// marked offline every night and Tips & Checks reported nightfall as an outage.
        /// </summary>
        [Fact]
        public async Task Availability_is_not_judged_outside_daylight()
        {
            var sessy = Inverter(SessyInverterService.SessyProviderName, supportsCurtailment: false);

            // Never read, so the health check would call it offline on the timestamp alone.
            sessy.SetupProperty(s => s.IsAvailable, true);
            sessy.Setup(s => s.LastSuccessfulReadUtc).Returns(DateTime.MinValue);

            var manager = NewManager(ConfigFor(SessyInverterService.SessyProviderName),
                                     SolCalc.Data.SunlightLevel.Night, sessy.Object);

            await manager.CheckAvailabilityAsync();

            Assert.True(sessy.Object.IsAvailable);
        }

        [Fact]
        public async Task In_daylight_a_source_that_never_read_is_marked_offline()
        {
            var sessy = Inverter(SessyInverterService.SessyProviderName, supportsCurtailment: false);

            sessy.SetupProperty(s => s.IsAvailable, true);
            sessy.Setup(s => s.LastSuccessfulReadUtc).Returns(DateTime.MinValue);

            var manager = NewManager(ConfigFor(SessyInverterService.SessyProviderName),
                                     SolCalc.Data.SunlightLevel.Daylight, sessy.Object);

            await manager.CheckAvailabilityAsync();

            Assert.False(sessy.Object.IsAvailable);
        }

        // ── One source or the other ───────────────────────────────────────────

        [Fact]
        public void Sessy_and_an_inverter_together_leave_only_the_sessy_active()
        {
            var manager = NewManager(
                ConfigFor(SessyInverterService.SessyProviderName, "SolarEdge"),
                Inverter(SessyInverterService.SessyProviderName, supportsCurtailment: false).Object,
                Inverter("SolarEdge", supportsCurtailment: true).Object);

            Assert.Single(manager.ActiveInverterServices);
            Assert.Equal(SessyInverterService.SessyProviderName, manager.ActiveInverterServices[0].ProviderName);
        }

        [Fact]
        public void Two_inverters_without_a_sessy_both_stay_active()
        {
            var manager = NewManager(
                ConfigFor("SolarEdge", "Enphase"),
                Inverter("SolarEdge", supportsCurtailment: true).Object,
                Inverter("Enphase", supportsCurtailment: true).Object);

            Assert.Equal(2, manager.ActiveInverterServices.Count);
        }

        // ── Curtailment capability ────────────────────────────────────────────

        [Fact]
        public void Curtailment_is_impossible_with_only_the_sessy()
        {
            var manager = NewManager(
                ConfigFor(SessyInverterService.SessyProviderName),
                Inverter(SessyInverterService.SessyProviderName, supportsCurtailment: false).Object);

            Assert.False(manager.CurtailmentIsPossible);
        }

        [Fact]
        public void Curtailment_is_possible_with_an_inverter()
        {
            var manager = NewManager(
                ConfigFor("SolarEdge"),
                Inverter("SolarEdge", supportsCurtailment: true).Object);

            Assert.True(manager.CurtailmentIsPossible);
        }

        [Fact]
        public void Curtailment_is_impossible_without_any_solar()
        {
            var manager = NewManager(new PowerSystemsConfig());

            Assert.False(manager.CurtailmentIsPossible);
        }

        /// <summary>
        /// Nothing may be commanded, and LastSetpointW must stay unset — otherwise the UI reports a
        /// throttle percentage for an order that never left the building.
        /// </summary>
        [Fact]
        public async Task Throttling_a_read_only_source_commands_nothing()
        {
            var sessy = Inverter(SessyInverterService.SessyProviderName, supportsCurtailment: false);

            var manager = NewManager(ConfigFor(SessyInverterService.SessyProviderName), sessy.Object);

            await manager.ThrottleInverterToWatts(0.0);

            sessy.Verify(s => s.ThrottleInverterToPercentage(It.IsAny<ushort>()), Times.Never);
            Assert.Null(manager.LastSetpointW);
        }

        [Fact]
        public async Task Throttling_an_inverter_still_reaches_the_hardware()
        {
            var solarEdge = Inverter("SolarEdge", supportsCurtailment: true);

            var manager = NewManager(ConfigFor("SolarEdge"), solarEdge.Object);

            await manager.ThrottleInverterToWatts(0.0);

            solarEdge.Verify(s => s.ThrottleInverterToPercentage(It.IsAny<ushort>()), Times.Once);
            Assert.Equal(0.0, manager.LastSetpointW);
        }
    }
}

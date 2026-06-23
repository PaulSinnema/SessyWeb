using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SessyCommon.Configurations;
using SessyCommon.Enums;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.Statistics;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;

namespace SessyTests.Services
{
    public class EnergyStatisticsServiceTests
    {
        private readonly Mock<QuarterlyMeasurementDataService> _measurementMock;
        private readonly Mock<InverterMeasurementDataService> _inverterMeasurementMock;
        private readonly Mock<SolarDataService> _solarDataMock;
        private readonly Mock<InvestmentDataService> _investmentMock;
        private readonly Mock<EnergyHistoryDataService> _energyHistoryMock;
        private readonly Mock<EPEXPricesDataService> _epexMock;
        private readonly Mock<InvestmentGroupDataService> _groupMock;
        private readonly Mock<TimeZoneService> _timeZoneMock;
        private readonly Mock<ICalculationService> _calculationServiceMock;
        private readonly EnergyStatisticsService _sut;

        private static readonly DateTime PeriodStart = new DateTime(2026, 5, 1);
        private static readonly DateTime PeriodEnd = new DateTime(2026, 5, 31, 23, 45, 0);

        // ── Constructor / mock setup ─────────────────────────────────────────

        public EnergyStatisticsServiceTests()
        {
            var scopeFactoryMock = BuildScopeFactory();

            _measurementMock = new Mock<QuarterlyMeasurementDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _measurementMock.Setup(s => s.Get(It.IsAny<Func<IQueryable<QuarterlyMeasurement>, Task<QuarterlyMeasurement?>>>()))
                            .ReturnsAsync((QuarterlyMeasurement?)null);
            _investmentMock = new Mock<InvestmentDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _energyHistoryMock = new Mock<EnergyHistoryDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _energyHistoryMock.Setup(s => s.GetList(It.IsAny<Func<IQueryable<EnergyHistory>, Task<List<EnergyHistory>>>>()))
                              .ReturnsAsync(new List<EnergyHistory>());
            _energyHistoryMock.Setup(s => s.Get(It.IsAny<Func<IQueryable<EnergyHistory>, Task<EnergyHistory?>>>()))
                              .ReturnsAsync((EnergyHistory?)null);
            _epexMock = new Mock<EPEXPricesDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _epexMock.Setup(s => s.GetList(It.IsAny<Func<IQueryable<EPEXPrices>, Task<List<EPEXPrices>>>>()))
                     .ReturnsAsync(new List<EPEXPrices>());
            _epexMock.Setup(s => s.Get(It.IsAny<Func<IQueryable<EPEXPrices>, Task<EPEXPrices?>>>()))
                     .ReturnsAsync((EPEXPrices?)null);
            _groupMock = new Mock<InvestmentGroupDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _groupMock.Setup(s => s.GetList(It.IsAny<Func<IQueryable<InvestmentGroup>, Task<List<InvestmentGroup>>>>()))
                      .ReturnsAsync(new List<InvestmentGroup>());

            var timeZoneSettings = Options.Create(new SettingsConfig { Timezone = "Europe/Amsterdam" });
            _timeZoneMock = new Mock<TimeZoneService>(MockBehavior.Loose, timeZoneSettings);
            _timeZoneMock.Setup(t => t.Now).Returns(new DateTime(2026, 5, 31, 12, 0, 0));

            var heatPumpConfig = Options.Create(new HeatPumpConfig
            {
                AnnualGasConsumptionM3 = 950,
                GasPriceEurPerM3 = 1.45,
                GasStandingChargeEurPerYear = 185.0,
                InstallationDate = new DateTime(2024, 3, 1)
            });
            var settingsServiceMock = new Mock<SettingsService>(MockBehavior.Loose, null!, null!, Options.Create(new SettingsConfig()));
            settingsServiceMock.SetupGet(s => s.Current).Returns(new Settings());
            var powerSystemsConfig = Options.Create(new PowerSystemsConfig());

            // Mock via interfaces — avoids constructor issues with complex service dependencies.
            var epexPricesServiceMock = new Mock<IEPEXPricesService>();
            epexPricesServiceMock.Setup(s => s.CurrentGasPriceEurPerM3).Returns((double?)null);

            var gasPricesMock = new Mock<IGasPricesDataService>();
            gasPricesMock.Setup(s => s.GetAverageMarketPriceAsync(It.IsAny<DateTime?>()))
                         .ReturnsAsync((double?)null);
            gasPricesMock.Setup(s => s.GetAllAsync())
                         .ReturnsAsync(new List<SessyData.Model.GasPrice>());

            var calculationServiceMock = new Mock<ICalculationService>();
            _calculationServiceMock = calculationServiceMock;
            calculationServiceMock.Setup(s => s.CalculateGasPriceAsync(It.IsAny<double>()))
                                  .ReturnsAsync((double?)null);
            calculationServiceMock.Setup(s => s.CalculateEnergyPricesBatchAsync(It.IsAny<IEnumerable<DateTime>>()))
                                  .ReturnsAsync(new Dictionary<DateTime, EnergyPrice>());

            // Mock IBatteryContainer — 3 × 5400 Wh = 16200 Wh total capacity.
            var batteryContainerMock = new Mock<IBatteryContainer>();
            batteryContainerMock.Setup(b => b.GetTotalCapacity()).Returns(16200.0);

            var milpServiceMock = new Mock<IMilpService>();

            var consumptionMock = new Mock<ConsumptionDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            consumptionMock.Setup(s => s.GetList(It.IsAny<Func<IQueryable<Consumption>, Task<List<Consumption>>>>()))
                           .ReturnsAsync(new List<Consumption>());

            var scopeFactoryMock2 = BuildScopeFactory();
            _inverterMeasurementMock = new Mock<InverterMeasurementDataService>(MockBehavior.Loose, scopeFactoryMock2.Object);
            _inverterMeasurementMock.Setup(s => s.GetList(It.IsAny<Func<IQueryable<InverterMeasurement>, Task<List<InverterMeasurement>>>>()))
                                    .ReturnsAsync(new List<InverterMeasurement>());
            _solarDataMock = new Mock<SolarDataService>(MockBehavior.Loose, scopeFactoryMock2.Object);
            _solarDataMock.Setup(s => s.GetList(It.IsAny<Func<IQueryable<SolarData>, Task<List<SolarData>>>>()))
                          .ReturnsAsync(new List<SolarData>());

            _sut = new EnergyStatisticsService(
                _measurementMock.Object,
                _investmentMock.Object,
                _energyHistoryMock.Object,
                _epexMock.Object,
                _groupMock.Object,
                _timeZoneMock.Object,
                heatPumpConfig,
                settingsServiceMock.Object,
                powerSystemsConfig,
                epexPricesServiceMock.Object,
                gasPricesMock.Object,
                consumptionMock.Object,
                calculationServiceMock.Object,
                batteryContainerMock.Object,
                milpServiceMock.Object,
                null!,   // hardwareStatusService
                null!,   // planVsActualService
                _inverterMeasurementMock.Object,
                _solarDataMock.Object);
        }

        // ── Grid flow tests ──────────────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesGridFlowsCorrectly()
        {
            // 4 quarters: 100 Wh import and 50 Wh export each.
            // Total import = 0.4 kWh, total export = 0.2 kWh.
            // Grid now comes from EnergyHistory meter-reading deltas, not the measurement.
            SetupMeasurements(Enumerable.Range(0, 4).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15)
            }).ToList());

            SetupMeterReadings(Enumerable.Range(0, 4).Select(i =>
                (PeriodStart.AddMinutes(i * 15), 100.0, 50.0)));

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.4, result.TotalGridImportKWh, 3);
            Assert.Equal(0.2, result.TotalGridExportKWh, 3);
        }

        [Fact]
        public async Task GetEnergyStatistics_ReturnsZeroWhenNoMeasurements()
        {
            SetupMeasurements(new List<QuarterlyMeasurement>());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.0, result.TotalGridImportKWh);
            Assert.Equal(0.0, result.TotalGridExportKWh);
        }

        // ── Solar statistics tests ───────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesTotalSolarProductionCorrectly()
        {
            // 4 quarters × 1.0 kWh solar = 4.0 kWh total — from InverterMeasurements.
            var times = Enumerable.Range(0, 4).Select(i => PeriodStart.AddMinutes(i * 15)).ToList();
            SetupMeasurements(times.Select(t => new QuarterlyMeasurement { Time = t }).ToList());
            SetupInverterMeasurements(times.Select(t => new InverterMeasurement
            {
                Time = t,
                InverterId = "1",
                ProviderName = "Test",
                SolarProductionKWh = 1.0
            }).ToList());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(4.0, result.TotalSolarProductionKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfConsumptionCorrect()
        {
            // Solar = 1.0 kWh, import = 0, export = 0.2 kWh.
            // Battery discharged = 0, so battery contribution to export = 0.
            // Solar exported = max(0, 0.2 - 0) = 0.2 kWh.
            // Self consumed = 1.0 - 0.2 = 0.8 kWh = 80%.
            SetupMeasurements(new List<QuarterlyMeasurement>
            {
                new() { Time = PeriodStart }
            });
            SetupMeterReadings(new[] { (PeriodStart, 0.0, 200.0) });
            SetupInverterMeasurements(new List<InverterMeasurement>
            {
                new() { Time = PeriodStart, InverterId = "1", ProviderName = "Test", SolarProductionKWh = 1.0 }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.8, result.SelfConsumedSolarKWh, 2);
            Assert.Equal(80.0, result.SelfConsumptionPct, 0);
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfConsumption_CorrectWhenBatteryDischargesExport()
        {
            // Solar = 0.5 kWh, battery discharged = 0.4 kWh, total export = 0.6 kWh.
            // Battery contribution = min(0.4, 0.6) = 0.4.
            // Solar exported = max(0, 0.6 - 0.4) = 0.2 kWh.
            // Self consumed = 0.5 - 0.2 = 0.3 kWh = 60%.
            SetupMeasurements(new List<QuarterlyMeasurement>
            {
                new()
                {
                    Time = PeriodStart,
                    BatteryPowerWatts = 1600, // discharging: 1600W * 0.25h / 1000 = 0.4 kWh
                    BatteryMode = Modes.Discharging,
                    IsReliable = true
                }
            });
            SetupMeterReadings(new[] { (PeriodStart, 0.0, 600.0) });
            SetupInverterMeasurements(new List<InverterMeasurement>
            {
                new() { Time = PeriodStart, InverterId = "1", ProviderName = "Test", SolarProductionKWh = 0.5 }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.3, result.SelfConsumedSolarKWh, 2);
        }

        // ── Battery statistics tests ─────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryChargedCorrectly()
        {
            // 4 quarters charging at -1800W = 4 * 1800 * 0.25 / 1000 = 1.8 kWh.
            SetupMeasurements(Enumerable.Range(0, 4).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = -1800,
                BatteryMode = Modes.Charging,
                IsReliable = true
            }).ToList());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.8, result.TotalBatteryChargedKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryDischargedCorrectly()
        {
            // 4 quarters discharging at 1500W = 4 * 1500 * 0.25 / 1000 = 1.5 kWh.
            SetupMeasurements(Enumerable.Range(0, 4).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = 1500,
                BatteryMode = Modes.Discharging,
                IsReliable = true
            }).ToList());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.5, result.TotalBatteryDischargedKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryCyclesCorrectly()
        {
            // 36 quarters * 1800W * 0.25h / 1000 = 16.2 kWh = 1 full cycle.
            SetupMeasurements(Enumerable.Range(0, 36).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = -1800,
                BatteryMode = Modes.Charging,
                IsReliable = true
            }).ToList());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.0, result.BatteryCycles, 1);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesRoundTripEfficiencyCorrectly()
        {
            // Charge 10 kWh (40 quarters × 1000W), discharge 9.5 kWh (38 quarters × 1000W).
            // Efficiency = 9.5 / 10 = 95%.
            var measurements = new List<QuarterlyMeasurement>();

            measurements.AddRange(Enumerable.Range(0, 40).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = -1000,
                BatteryMode = Modes.Charging,
                IsReliable = true
            }));

            measurements.AddRange(Enumerable.Range(40, 38).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = 1000,
                BatteryMode = Modes.Discharging,
                IsReliable = true
            }));

            SetupMeasurements(measurements);

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(95.0, result.BatteryRoundTripEfficiencyPct, 0);
        }

        [Fact]
        public async Task GetEnergyStatistics_ExcludesUnreliableRecordsFromEfficiency()
        {
            // 10 kWh reliable + 4 kWh unreliable charged, 9.5 kWh reliable discharged.
            // Reduced unreliable count to 16 quarters so all data fits within one day
            // (40 + 16 + 38 = 94 quarters, last at index 93 = 23:15, no overflow to next day).
            // Efficiency = 9.5 / 10 = 95% (unreliable excluded).
            var measurements = new List<QuarterlyMeasurement>();

            measurements.AddRange(Enumerable.Range(0, 40).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = -1000,
                BatteryMode = Modes.Charging,
                IsReliable = true
            }));

            measurements.AddRange(Enumerable.Range(40, 16).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = -1000,
                BatteryMode = Modes.Charging,
                IsReliable = false
            }));

            measurements.AddRange(Enumerable.Range(56, 38).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = 1000,
                BatteryMode = Modes.Discharging,
                IsReliable = true
            }));

            SetupMeasurements(measurements);

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(14.0, result.TotalBatteryChargedKWh, 1);
            Assert.Equal(10.0, result.ReliableBatteryChargedKWh, 1);
            Assert.Equal(9.5, result.ReliableBatteryDischargedKWh, 1);
            Assert.Equal(95.0, result.BatteryRoundTripEfficiencyPct, 0);
        }

        // ── Consumption tests ────────────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesConsumptionViaEnergyBalance()
        {
            // Consumption = import + solar - export = 500 + 200 - 100 = 600 Wh = 0.6 kWh.
            SetupMeasurements(new List<QuarterlyMeasurement>
            {
                new() { Time = PeriodStart }
            });
            SetupMeterReadings(new[] { (PeriodStart, 500.0, 100.2) });

            // Solar production = 0.2 kWh for this quarter.
            SetupInverterMeasurements(new List<InverterMeasurement>
            {
                new() { Time = PeriodStart, SolarProductionKWh = 0.2 }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.6, result.TotalConsumptionKWh, 3);
        }

        [Fact]
        public async Task GetEnergyStatistics_WeekdayWeekendSplitCorrect()
        {
            // May 1 2026 = Friday (weekday), May 2 = Saturday (weekend).
            // Each day gets 00:00 and 23:45 measurements so the incomplete-day filter
            // does not remove either day (lastTime.TimeOfDay = 23:45 is not < 23:45).
            var measurements = new List<QuarterlyMeasurement>
            {
                new() { Time = new DateTime(2026, 5, 1,  0,  0, 0) },
                new() { Time = new DateTime(2026, 5, 1, 23, 45, 0) },
                new() { Time = new DateTime(2026, 5, 2,  0,  0, 0) },
                new() { Time = new DateTime(2026, 5, 2, 23, 45, 0) }
            };
            SetupMeasurements(measurements);
            SetupMeterReadings(new[]
            {
                (new DateTime(2026, 5, 1,  0,  0, 0), 500.0, 0.0),
                (new DateTime(2026, 5, 1, 23, 45, 0), 500.0, 0.0),
                (new DateTime(2026, 5, 2,  0,  0, 0), 250.0, 0.0),
                (new DateTime(2026, 5, 2, 23, 45, 0), 250.0, 0.0)
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.0, result.WeekdayConsumptionKWh, 2);
            Assert.Equal(0.5, result.WeekendConsumptionKWh, 2);
        }

        // ── StatisticsFromDate tests ─────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_ClampsStartToStatisticsFromDate()
        {
            // StatisticsFromDate = May 15 — measurements before that should be excluded.
            var fromDate = new DateTime(2026, 5, 15);

            var scopeFactoryMock = BuildScopeFactory();
            var measurementMock = new Mock<QuarterlyMeasurementDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            var investmentMock = new Mock<InvestmentDataService>(MockBehavior.Loose, scopeFactoryMock.Object);

            // Only return measurements from May 15 onwards.
            // Each day has a 00:00 and 23:45 measurement so the incomplete-day filter
            // retains both days (lastTime.TimeOfDay = 23:45 is not < 23:45).
            measurementMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<QuarterlyMeasurement>, Task<List<QuarterlyMeasurement>>>>()))
                .ReturnsAsync(new List<QuarterlyMeasurement>
                {
                    new() { Time = fromDate },
                    new() { Time = fromDate.AddHours(23).AddMinutes(45) },
                    new() { Time = fromDate.AddDays(10) },
                    new() { Time = fromDate.AddDays(10).AddHours(23).AddMinutes(45) }
                });

            investmentMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<Investment>, Task<List<Investment>>>>()))
                .ReturnsAsync(new List<Investment>());

            var settingsServiceMock2 = new Mock<SettingsService>(MockBehavior.Loose, null!, null!, Options.Create(new SettingsConfig()));
            settingsServiceMock2.SetupGet(s => s.Current).Returns(new Settings { StatisticsFromDate = fromDate });
            var heatPumpConfig = Options.Create(new HeatPumpConfig());
            var energyHistoryMock = new Mock<EnergyHistoryDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            // Cumulative meter readings whose deltas give 150/150/100/100 Wh import per quarter.
            // A seed reading 15 min before the first flow makes the first delta correct.
            energyHistoryMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<EnergyHistory>, Task<List<EnergyHistory>>>>()))
                .ReturnsAsync(new List<EnergyHistory>
                {
                    new() { Time = fromDate.AddMinutes(-15),                          ConsumedTariff1 = 0 },
                    new() { Time = fromDate,                                          ConsumedTariff1 = 150 },
                    new() { Time = fromDate.AddHours(23).AddMinutes(45),             ConsumedTariff1 = 300 },
                    new() { Time = fromDate.AddDays(10),                             ConsumedTariff1 = 400 },
                    new() { Time = fromDate.AddDays(10).AddHours(23).AddMinutes(45), ConsumedTariff1 = 500 }
                });
            var epexMock = new Mock<EPEXPricesDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            var groupMock2 = new Mock<InvestmentGroupDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            var powerSystemsConfig = Options.Create(new PowerSystemsConfig());

            var epexPricesServiceMock2 = new Mock<IEPEXPricesService>();
            epexPricesServiceMock2.Setup(s => s.CurrentGasPriceEurPerM3).Returns((double?)null);

            var gasPricesMock2 = new Mock<IGasPricesDataService>();
            gasPricesMock2.Setup(s => s.GetAverageMarketPriceAsync(It.IsAny<DateTime?>())).ReturnsAsync((double?)null);
            gasPricesMock2.Setup(s => s.GetAllAsync()).ReturnsAsync(new List<SessyData.Model.GasPrice>());

            var calculationServiceMock2 = new Mock<ICalculationService>();
            calculationServiceMock2.Setup(s => s.CalculateGasPriceAsync(It.IsAny<double>())).ReturnsAsync((double?)null);
            calculationServiceMock2.Setup(s => s.CalculateEnergyPricesBatchAsync(It.IsAny<IEnumerable<DateTime>>()))
                                   .ReturnsAsync(new Dictionary<DateTime, EnergyPrice>());

            var batteryContainerMock2 = new Mock<IBatteryContainer>();
            batteryContainerMock2.Setup(b => b.GetTotalCapacity()).Returns(16200.0);

            var milpServiceMock2 = new Mock<IMilpService>();

            var consumptionMock2 = new Mock<ConsumptionDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            consumptionMock2.Setup(s => s.GetList(It.IsAny<Func<IQueryable<Consumption>, Task<List<Consumption>>>>()))
                            .ReturnsAsync(new List<Consumption>());

            var sut = new EnergyStatisticsService(
                measurementMock.Object,
                investmentMock.Object,
                energyHistoryMock.Object,
                epexMock.Object,
                groupMock2.Object,
                _timeZoneMock.Object,
                heatPumpConfig,
                settingsServiceMock2.Object,
                powerSystemsConfig,
                epexPricesServiceMock2.Object,
                gasPricesMock2.Object,
                consumptionMock2.Object,
                calculationServiceMock2.Object,
                batteryContainerMock2.Object,
                milpServiceMock2.Object,
                null!,   // hardwareStatusService
                null!,   // planVsActualService
                _inverterMeasurementMock.Object,
                _solarDataMock.Object);

            // Request full month — StatisticsFromDate clips it to May 15.
            var result = await sut.GetEnergyStatisticsAsync(DateTime.MinValue, PeriodEnd);

            // Total import = (300 + 200) Wh / 1000 = 0.5 kWh.
            Assert.Equal(0.5, result.TotalGridImportKWh, 2);
        }

        // ── Financial statistics tests ───────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesArbitrageProfitCorrectly()
        {
            // Charge 1 kWh at 0.10 EUR: cost = 0.10 EUR.
            // Discharge 1 kWh, all exported at sell 0.30: value = 0.30 EUR.
            // Arbitrage = 0.30 - 0.10 = 0.20 EUR.
            var measurements = new List<QuarterlyMeasurement>();

            // 4 quarters charging at 1000W = 1.0 kWh
            measurements.AddRange(Enumerable.Range(0, 4).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = -1000,
                BatteryMode = Modes.Charging
            }));

            // 4 quarters discharging at 1000W = 1.0 kWh, fully exported to grid.
            measurements.AddRange(Enumerable.Range(4, 4).Select(i => new QuarterlyMeasurement
            {
                Time = PeriodStart.AddMinutes(i * 15),
                BatteryPowerWatts = 1000,
                BatteryMode = Modes.Discharging
            }));

            SetupMeasurements(measurements);
            SetupPrices(0.10, 0.30);

            // 250 Wh export per discharging quarter so all discharge counts as export.
            SetupMeterReadings(Enumerable.Range(4, 4).Select(i =>
                (PeriodStart.AddMinutes(i * 15), 0.0, 250.0)));

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.20, result.ArbitrageProfitEur, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfSufficiencyPctClamped()
        {
            // Grid import > consumption due to battery charging → clamp to [0, 100].
            SetupMeasurements(new List<QuarterlyMeasurement>
            {
                new()
                {
                    Time = PeriodStart,
                    BatteryPowerWatts = -1800, // charging from grid
                    BatteryMode = Modes.Charging
                }
            });
            SetupMeterReadings(new[] { (PeriodStart, 2000.0, 0.0) });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.True(result.SelfSufficiencyPct >= 0.0);
            Assert.True(result.SelfSufficiencyPct <= 100.0);
        }

        // ── Investment statistics tests ──────────────────────────────────────

        [Fact]
        public async Task GetInvestmentStatistics_CalculatesNetInvestmentCorrectly()
        {
            SetupGroups(new List<InvestmentGroup>
            {
                new() { Id = 1, Name = "Solar",   Category = InvestmentCategory.Solar },
                new() { Id = 2, Name = "Storage", Category = InvestmentCategory.Storage },
                new() { Id = 3, Name = "HeatPump",Category = InvestmentCategory.HeatPump }
            });
            SetupInvestments(new List<Investment>
            {
                new() { InvestmentGroupId = 1, AmountEur = 10000, SubsidyEur = 2000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 25 },
                new() { InvestmentGroupId = 2, AmountEur = 8000,  SubsidyEur = 0,    PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 15 },
                new() { InvestmentGroupId = 3, AmountEur = 6000,  SubsidyEur = 1000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 20 }
            });
            SetupMeasurements(new List<QuarterlyMeasurement>());

            var result = await _sut.GetInvestmentStatisticsAsync();

            Assert.Equal(21000.0, result.TotalNetInvestmentEur, 2);
        }

        [Fact]
        public async Task GetInvestmentStatistics_ReturnsEmptyWhenNoInvestments()
        {
            SetupInvestments(new List<Investment>());
            SetupMeasurements(new List<QuarterlyMeasurement>());

            var result = await _sut.GetInvestmentStatisticsAsync();

            Assert.Equal(0.0, result.TotalNetInvestmentEur);
            Assert.Empty(result.CategoryBreakdown);
        }

        // ── Monthly trend tests ──────────────────────────────────────────────

        [Fact]
        public async Task GetMonthlyTrends_ReturnsCorrectNumberOfMonths()
        {
            SetupMeasurements(new List<QuarterlyMeasurement>());

            var result = await _sut.GetMonthlyTrendsAsync(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 5, 31));

            Assert.Equal(5, result.Count);
        }

        [Fact]
        public async Task GetMonthlyTrends_MonthsAreCorrectlyLabeled()
        {
            SetupMeasurements(new List<QuarterlyMeasurement>());

            var result = await _sut.GetMonthlyTrendsAsync(
                new DateTime(2026, 3, 1),
                new DateTime(2026, 5, 31));

            Assert.Equal(3, result[0].Month);
            Assert.Equal(4, result[1].Month);
            Assert.Equal(5, result[2].Month);
        }

        // ── Derived metric tests ─────────────────────────────────────────────

        [Fact]
        public void EnergyStatistics_MonthlySavingsExtrapolatedCorrectly()
        {
            var stats = new EnergyStatistics
            {
                PeriodStart = new DateTime(2026, 5, 1),
                PeriodEnd = new DateTime(2026, 5, 31),
                ActualEnergyCostEur = 40,
                BaselineEnergyCostEur = 100
            };

            Assert.Equal(60.0, stats.TotalSavingsEur, 2);
            Assert.Equal(60.0, stats.MonthlySavingsEur, 0);
            Assert.Equal(730.0, stats.AnnualSavingsEur, 0);
        }

        [Fact]
        public void EnergyStatistics_GridDependencyPctCorrect()
        {
            var stats = new EnergyStatistics
            {
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                TotalConsumptionKWh = 100,
                TotalGridImportKWh = 30
            };

            Assert.Equal(30.0, stats.GridDependencyPct, 1);
        }

        [Fact]
        public void InvestmentStatistics_ProjectedPaybackYearsCorrect()
        {
            var stats = new InvestmentStatistics
            {
                TotalNetInvestmentEur = 21000,
                TotalRealizedSavingsEur = 0,
                ProjectedAnnualSavingsEur = 3000
            };

            Assert.Equal(7.0, stats.ProjectedPaybackYears, 1);
        }

        [Fact]
        public void InvestmentStatistics_RecoveredPctCorrect()
        {
            var stats = new InvestmentStatistics
            {
                TotalNetInvestmentEur = 21000,
                TotalRealizedSavingsEur = 7000,
                ProjectedAnnualSavingsEur = 3000
            };

            Assert.Equal(33.3, stats.RecoveredPct, 0);
            Assert.Equal(14000.0, stats.RemainingInvestmentEur, 1);
        }

        // ── BatteryArbitrageMilp tests ────────────────────────────────────────

        [Fact]
        public void BatteryArbitrageMilp_NettingOn_PlansBothChargeAndDischarge()
        {
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints =
                Enumerable.Range(0, 8).Select(i => new PricePoint(
                    baseTime.AddMinutes(i * 15),
                    BuyEurPerKWh: 0.05, SellEurPerKWh: 0.05, NetLoadWh: 0, SolarSurplusWh: 0))
                .Concat(Enumerable.Range(8, 8).Select(i => new PricePoint(
                    baseTime.AddMinutes(i * 15),
                    BuyEurPerKWh: 0.30, SellEurPerKWh: 0.30, NetLoadWh: 0, SolarSurplusWh: 0)))
                .ToList();

            var bounds = MakeBounds(pricePoints.Select(p => p.Start));
            var result = BatteryArbitrageMilp.Solve(pricePoints, MakeSpec(4.0), MakeOpt(0.02), bounds);

            Assert.True(result!.Plan.Any(p => p.Mode == ActionMode.Charge), "Expected charging");
            Assert.True(result!.Plan.Any(p => p.Mode == ActionMode.Discharge), "Expected discharging");
        }

        [Fact]
        public void BatteryArbitrageMilp_NettingOff_SelfUseValueDrivesDischarge()
        {
            var baseTime = new DateTime(2027, 1, 1);
            // High buy price + positive net load: own-use discharge should be profitable.
            var pricePoints = new List<PricePoint>
            {
                new(baseTime,                BuyEurPerKWh: 0.05, SellEurPerKWh: 0.03, NetLoadWh: 500,  SolarSurplusWh: 0),
                new(baseTime.AddMinutes(15), BuyEurPerKWh: 0.05, SellEurPerKWh: 0.03, NetLoadWh: 500,  SolarSurplusWh: 0),
                new(baseTime.AddMinutes(30), BuyEurPerKWh: 0.25, SellEurPerKWh: 0.03, NetLoadWh: 2000, SolarSurplusWh: 0),
                new(baseTime.AddMinutes(45), BuyEurPerKWh: 0.25, SellEurPerKWh: 0.03, NetLoadWh: 2000, SolarSurplusWh: 0),
            };

            var bounds = MakeBounds(pricePoints.Select(p => p.Start));
            var result = BatteryArbitrageMilp.Solve(pricePoints, MakeSpec(8.0), MakeOpt(0.02), bounds);

            Assert.True(result!.Plan.Any(p => p.Mode == ActionMode.Discharge),
                "Expected discharge during high-consumption quarters");
        }

        [Fact]
        public void BatteryArbitrageMilp_NettingOff_DoesNotChargeWhenCyclingNotProfitable()
        {
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints = Enumerable.Range(0, 4).Select(i => new PricePoint(
                baseTime.AddMinutes(i * 15),
                BuyEurPerKWh: 0.03, SellEurPerKWh: 0.03, NetLoadWh: 0, SolarSurplusWh: 0
            )).ToList();

            var spec = new BatterySpec(
                CapacityKWh: 16.2, InitialSocKWh: 0.0,
                MaxChargeKW: 5.4, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.95, DischargeEfficiency: 0.95);

            var bounds = MakeBounds(pricePoints.Select(p => p.Start));
            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, MakeOpt(0.20), bounds);

            Assert.False(result!.Plan.Any(p => p.Mode == ActionMode.Charge), "Expected no charging");
            Assert.False(result!.Plan.Any(p => p.Mode == ActionMode.Discharge), "Expected no discharging");
        }

        [Fact]
        public void BatteryArbitrageMilp_SolvesWithoutThrowingWhenSolarSurplusIsZero()
        {
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints = new List<PricePoint>
            {
                new(baseTime,                BuyEurPerKWh: 0.05, SellEurPerKWh: 0.25, NetLoadWh: 0, SolarSurplusWh: 0),
                new(baseTime.AddMinutes(15), BuyEurPerKWh: 0.05, SellEurPerKWh: 0.25, NetLoadWh: 0, SolarSurplusWh: 0),
            };

            var bounds = MakeBounds(pricePoints.Select(p => p.Start));
            var result = BatteryArbitrageMilp.Solve(pricePoints, MakeSpec(8.0), MakeOpt(0.02), bounds);

            Assert.NotNull(result);
        }

        // ── Helper methods ───────────────────────────────────────────────────

        private void SetupMeasurements(List<QuarterlyMeasurement> data)
        {
            _measurementMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<QuarterlyMeasurement>, Task<List<QuarterlyMeasurement>>>>()))
                .ReturnsAsync(data);
        }

        /// <summary>
        /// Builds cumulative EnergyHistory meter readings from the desired per-quarter grid
        /// flows and feeds them to the mock. GetMeasurementsAsync derives grid import/export
        /// as the delta between consecutive readings, so a reading is emitted one quarter
        /// before the first flow to seed that first delta. Each tuple is (time, importWh,
        /// exportWh) describing the flow during the quarter that ENDS at the given time.
        /// </summary>
        /// <summary>
        /// Makes the calculation service return the same buy/sell price for every quarter,
        /// so tests that assert on priced values are independent of EPEX data.
        /// </summary>
        private void SetupPrices(double buy, double sell)
        {
            _calculationServiceMock
                .Setup(s => s.CalculateEnergyPricesBatchAsync(It.IsAny<IEnumerable<DateTime>>()))
                .ReturnsAsync((IEnumerable<DateTime> times) =>
                    times.Distinct().ToDictionary(t => t, _ => new EnergyPrice(buy, sell)));
        }

        private void SetupMeterReadings(IEnumerable<(DateTime time, double importWh, double exportWh)> flows)
        {
            var list = flows.OrderBy(f => f.time).ToList();
            var readings = new List<EnergyHistory>();

            double cumImport = 0.0;
            double cumExport = 0.0;

            if (list.Count > 0)
            {
                // Seed reading one quarter before the first flow so the first delta is correct.
                readings.Add(new EnergyHistory
                {
                    Time = list[0].time.AddMinutes(-15),
                    ConsumedTariff1 = cumImport,
                    ProducedTariff1 = cumExport
                });
            }

            foreach (var (time, importWh, exportWh) in list)
            {
                cumImport += importWh;
                cumExport += exportWh;
                readings.Add(new EnergyHistory
                {
                    Time = time,
                    ConsumedTariff1 = cumImport,
                    ProducedTariff1 = cumExport
                });
            }

            _energyHistoryMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<EnergyHistory>, Task<List<EnergyHistory>>>>()))
                .ReturnsAsync(readings);
        }

        private void SetupInverterMeasurements(List<InverterMeasurement> data)
        {
            _inverterMeasurementMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<InverterMeasurement>, Task<List<InverterMeasurement>>>>()))
                .ReturnsAsync(data);
        }

        private void SetupInvestments(List<Investment> data)
        {
            _investmentMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<Investment>, Task<List<Investment>>>>()))
                .ReturnsAsync(data);
        }

        private void SetupGroups(List<InvestmentGroup> data)
        {
            _groupMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<InvestmentGroup>, Task<List<InvestmentGroup>>>>()))
                .ReturnsAsync(data);
        }

        private static Mock<IServiceScopeFactory> BuildScopeFactory()
        {
            var innerScopeFactory = new Mock<IServiceScopeFactory>();
            var dbHelperMock = new Mock<DbHelper>(MockBehavior.Loose, innerScopeFactory.Object);

            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(DbHelper))).Returns(dbHelperMock.Object);

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(provider.Object);

            var factory = new Mock<IServiceScopeFactory>();
            factory.Setup(f => f.CreateScope()).Returns(scope.Object);
            innerScopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            return factory;
        }

        private static BatterySpec MakeSpec(double initialSocKWh) => new BatterySpec(
            CapacityKWh: 16.2, InitialSocKWh: initialSocKWh,
            MaxChargeKW: 5.4, MaxDischargeKW: 5.1,
            ChargeEfficiency: 0.95, DischargeEfficiency: 0.95);

        private static SessyOptions MakeOpt(double cycleCost) => new SessyOptions(
            QuarterMinutes: 15,
            CycleCostEurPerKWh: cycleCost,
            TimeLimitMs: 5000);

        private static IReadOnlyList<SocBound> MakeBounds(IEnumerable<DateTime> times, double minKWh = 0.0, double maxKWh = 16.2)
            => times.Select(t => new SocBound(t, minKWh, maxKWh)).ToList();
    }
}
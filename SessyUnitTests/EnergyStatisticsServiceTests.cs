using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SessyData.Helpers;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Optimization;
using SessyController.Services.Statistics;
using SessyData.Model;
using SessyData.Services;
using Xunit;

namespace SessyTests.Services
{
    public class EnergyStatisticsServiceTests
    {
        private readonly Mock<EnergyHistoryDataService> _energyHistoryMock;
        private readonly Mock<PerformanceDataService> _performanceMock;
        private readonly Mock<InvestmentDataService> _investmentMock;
        private readonly Mock<TimeZoneService> _timeZoneMock;
        private readonly EnergyStatisticsService _sut;

        private static readonly DateTime PeriodStart = new DateTime(2026, 5, 1);
        private static readonly DateTime PeriodEnd = new DateTime(2026, 5, 31, 23, 45, 0);

        public EnergyStatisticsServiceTests()
        {
            // ServiceBase<T> calls serviceScopeFactory.CreateScope() then
            // GetRequiredService<DbHelper>() in its constructor.
            // Build the full mock chain: scopeFactory → scope → provider → DbHelper.
            var innerScopeFactoryMock = new Mock<IServiceScopeFactory>();

            var dbHelperMock = new Mock<DbHelper>(MockBehavior.Loose, innerScopeFactoryMock.Object);

            var providerMock = new Mock<IServiceProvider>();
            providerMock
                .Setup(p => p.GetService(typeof(DbHelper)))
                .Returns(dbHelperMock.Object);

            var scopeMock = new Mock<IServiceScope>();
            scopeMock.Setup(s => s.ServiceProvider).Returns(providerMock.Object);

            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);
            innerScopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

            _energyHistoryMock = new Mock<EnergyHistoryDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _performanceMock = new Mock<PerformanceDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            _investmentMock = new Mock<InvestmentDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            // TimeZoneService requires IOptions<SettingsConfig> — provide a minimal config.
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

            // StatisticsFromDate not set — all data is included.
            var settingsConfig = Options.Create(new SettingsConfig());

            _sut = new EnergyStatisticsService(
                _energyHistoryMock.Object,
                _performanceMock.Object,
                _investmentMock.Object,
                _timeZoneMock.Object,
                heatPumpConfig,
                settingsConfig);
        }

        // ── Grid flow tests ──────────────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesGridImportCorrectly()
        {
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 1000, ConsumedTariff2 = 500, ProducedTariff1 = 100, ProducedTariff2 = 50 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 1150, ConsumedTariff2 = 600, ProducedTariff1 = 200, ProducedTariff2 = 80 }
            });
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // P1 tands are in Wh / 1000 → kWh: (150+100)/1000 = 0.25, (100+30)/1000 = 0.13
            Assert.Equal(0.25, result.TotalGridImportKWh, 2);
            Assert.Equal(0.13, result.TotalGridExportKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_ClampsNegativeGridFlowsToZero()
        {
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 1000, ConsumedTariff2 = 500, ProducedTariff1 = 200, ProducedTariff2 = 80 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 900,  ConsumedTariff2 = 400, ProducedTariff1 = 100, ProducedTariff2 = 50 }
            });
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.0, result.TotalGridImportKWh);
            Assert.Equal(0.0, result.TotalGridExportKWh);
        }

        [Fact]
        public async Task GetEnergyStatistics_ReturnsZeroWhenNoHistory()
        {
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.0, result.TotalGridImportKWh);
            Assert.Equal(0.0, result.TotalGridExportKWh);
        }

        // ── Solar statistics tests ───────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesTotalSolarProductionCorrectly()
        {
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                SolarPowerPerQuarterHour = 1.0 },
                new() { Time = PeriodStart.AddMinutes(15), SolarPowerPerQuarterHour = 1.0 },
                new() { Time = PeriodStart.AddMinutes(30), SolarPowerPerQuarterHour = 1.0 },
                new() { Time = PeriodStart.AddMinutes(45), SolarPowerPerQuarterHour = 1.0 }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(4.0, result.TotalSolarProductionKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesSelfConsumedSolarCorrectly()
        {
            // 4 quarters with 1 kWh solar and 800W consumption.
            // Per quarter: min(1.0, 800 * 0.25 / 1000) = min(1.0, 0.2) = 0.2 kWh.
            // Total = 4 * 0.2 = 0.8 kWh self-consumed.
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 800 },
                new() { Time = PeriodStart.AddMinutes(15), SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 800 },
                new() { Time = PeriodStart.AddMinutes(30), SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 800 },
                new() { Time = PeriodStart.AddMinutes(45), SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 800 }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(4.0, result.TotalSolarProductionKWh, 1);
            Assert.Equal(0.8, result.SelfConsumedSolarKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfConsumptionPct_50Percent()
        {
            // 4 quarters with 1 kWh solar and 2000W consumption.
            // Per quarter: min(1.0, 2000 * 0.25 / 1000) = min(1.0, 0.5) = 0.5 kWh.
            // Total self-consumed = 2.0 / 4.0 = 50%.
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 2000 },
                new() { Time = PeriodStart.AddMinutes(15), SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 2000 },
                new() { Time = PeriodStart.AddMinutes(30), SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 2000 },
                new() { Time = PeriodStart.AddMinutes(45), SolarPowerPerQuarterHour = 1.0, EstimatedConsumptionPerQuarterHour = 2000 }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(50.0, result.SelfConsumptionPct, 0);
        }

        // ── Battery statistics tests ─────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryChargedCorrectly_UsingBatteryPowerWatts()
        {
            // BatteryPowerWatts negative = charging. 4 * 1800W * 0.25h / 1000 = 1.8 kWh.
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                Charging = true, BatteryPowerWatts = -1800, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(15), Charging = true, BatteryPowerWatts = -1800, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(30), Charging = true, BatteryPowerWatts = -1800, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(45), Charging = true, BatteryPowerWatts = -1800, IsReliable = true }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.8, result.TotalBatteryChargedKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryDischargedCorrectly_UsingBatteryPowerWatts()
        {
            // BatteryPowerWatts positive = discharging. 4 * 1500W * 0.25h / 1000 = 1.5 kWh.
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                Discharging = true, BatteryPowerWatts = 1500, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(15), Discharging = true, BatteryPowerWatts = 1500, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(30), Discharging = true, BatteryPowerWatts = 1500, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(45), Discharging = true, BatteryPowerWatts = 1500, IsReliable = true }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.5, result.TotalBatteryDischargedKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryCyclesCorrectly()
        {
            // 36 quarters * 1800W * 0.25h / 1000 = 16.2 kWh = 1 full cycle.
            var performance = Enumerable.Range(0, 36)
                .Select(i => new Performance
                {
                    Time = PeriodStart.AddMinutes(i * 15),
                    Charging = true,
                    BatteryPowerWatts = -1800,
                    IsReliable = true
                })
                .ToList();

            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(performance);

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.0, result.BatteryCycles, 1);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesRoundTripEfficiencyCorrectly()
        {
            // Charge 10 kWh, discharge 9.5 kWh → 95% efficiency.
            SetupEnergyHistory(new List<EnergyHistory>());

            var performance = new List<Performance>();

            performance.AddRange(Enumerable.Range(0, 40).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Charging = true,
                BatteryPowerWatts = -1000,
                IsReliable = true
            }));

            performance.AddRange(Enumerable.Range(40, 38).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Discharging = true,
                BatteryPowerWatts = 1000,
                IsReliable = true
            }));

            SetupPerformance(performance);

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(95.0, result.BatteryRoundTripEfficiencyPct, 0);
        }

        [Fact]
        public async Task GetEnergyStatistics_ExcludesUnreliableRecordsFromEfficiency()
        {
            // 10 kWh reliable + 5 kWh unreliable charged, 9.5 kWh reliable discharged.
            // Efficiency = 9.5 / 10 = 95% (unreliable excluded).
            // TotalCharged = 15 kWh (all records included for energy balance).
            SetupEnergyHistory(new List<EnergyHistory>());

            var performance = new List<Performance>();

            performance.AddRange(Enumerable.Range(0, 40).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Charging = true,
                BatteryPowerWatts = -1000,
                IsReliable = true
            }));

            performance.AddRange(Enumerable.Range(40, 20).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Charging = true,
                BatteryPowerWatts = -1000,
                IsReliable = false
            }));

            performance.AddRange(Enumerable.Range(60, 38).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Discharging = true,
                BatteryPowerWatts = 1000,
                IsReliable = true
            }));

            SetupPerformance(performance);

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(15.0, result.TotalBatteryChargedKWh, 1);
            Assert.Equal(10.0, result.ReliableBatteryChargedKWh, 1);
            Assert.Equal(9.5, result.ReliableBatteryDischargedKWh, 1);
            Assert.Equal(95.0, result.BatteryRoundTripEfficiencyPct, 0);
        }

        [Fact]
        public async Task GetEnergyStatistics_FallsBackToChargeLeftDeltaForLegacyRecords()
        {
            // Legacy records: BatteryPowerWatts = 0, consecutive quarters 15 min apart.
            // ChargeLeft delta: 1450 - 1000 = 450 Wh, 1900 - 1450 = 450 Wh → 0.9 kWh total.
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                Charging = true, BatteryPowerWatts = 0, ChargeLeft = 1000, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(15), Charging = true, BatteryPowerWatts = 0, ChargeLeft = 1450, IsReliable = true },
                new() { Time = PeriodStart.AddMinutes(30), Charging = true, BatteryPowerWatts = 0, ChargeLeft = 1900, IsReliable = true }
            });

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(0.9, result.TotalBatteryChargedKWh, 2);
        }

        // ── StatisticsFromDate tests ─────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_ClampsStartToStatisticsFromDate()
        {
            // StatisticsFromDate = May 15 — data before that date must be excluded.
            var fromDate = new DateTime(2026, 5, 15);
            var settingsConfig = Options.Create(new SettingsConfig { StatisticsFromDate = fromDate });
            var heatPumpConfig = Options.Create(new HeatPumpConfig());

            // Re-use the same mock infrastructure as the test class constructor.
            var innerScopeFactoryMock2 = new Mock<IServiceScopeFactory>();
            var dbHelperMock2 = new Mock<DbHelper>(MockBehavior.Loose, innerScopeFactoryMock2.Object);
            var providerMock2 = new Mock<IServiceProvider>();
            providerMock2
                .Setup(p => p.GetService(typeof(DbHelper)))
                .Returns(dbHelperMock2.Object);
            var scopeMock2 = new Mock<IServiceScope>();
            scopeMock2.Setup(s => s.ServiceProvider).Returns(providerMock2.Object);
            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock2.Object);
            innerScopeFactoryMock2.Setup(f => f.CreateScope()).Returns(scopeMock2.Object);

            var energyHistoryMock = new Mock<EnergyHistoryDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            var performanceMock = new Mock<PerformanceDataService>(MockBehavior.Loose, scopeFactoryMock.Object);
            var investmentMock = new Mock<InvestmentDataService>(MockBehavior.Loose, scopeFactoryMock.Object);

            energyHistoryMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<EnergyHistory>, Task<List<EnergyHistory>>>>()))
                .ReturnsAsync(new List<EnergyHistory>
                {
                    new() { Time = fromDate,             ConsumedTariff1 = 1000, ConsumedTariff2 = 0, ProducedTariff1 = 0, ProducedTariff2 = 0 },
                    new() { Time = fromDate.AddDays(10), ConsumedTariff1 = 1500, ConsumedTariff2 = 0, ProducedTariff1 = 0, ProducedTariff2 = 0 }
                });

            performanceMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<Performance>, Task<List<Performance>>>>()))
                .ReturnsAsync(new List<Performance>());

            var sut = new EnergyStatisticsService(
                energyHistoryMock.Object,
                performanceMock.Object,
                investmentMock.Object,
                _timeZoneMock.Object,
                heatPumpConfig,
                settingsConfig);

            var result = await sut.GetEnergyStatisticsAsync(DateTime.MinValue, PeriodEnd);

            // Grid import = (1500 - 1000) Wh / 1000 = 0.5 kWh
            Assert.Equal(0.5, result.TotalGridImportKWh, 1);
        }

        // ── Financial statistics tests ───────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesArbitrageProfitCorrectly()
        {
            // Buy 10 kWh at 0.10 EUR, sell 9.5 kWh at 0.30 EUR.
            // Arbitrage = 9.5 * 0.30 - 10 * 0.10 = 2.85 - 1.00 = 1.85 EUR.
            SetupEnergyHistory(new List<EnergyHistory>());

            var performance = new List<Performance>();

            performance.AddRange(Enumerable.Range(0, 40).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Charging = true,
                BuyingPrice = 0.10,
                SellingPrice = 0.30,
                EstimatedConsumptionPerQuarterHour = 1000
            }));

            performance.AddRange(Enumerable.Range(40, 38).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Discharging = true,
                BuyingPrice = 0.10,
                SellingPrice = 0.30,
                EstimatedConsumptionPerQuarterHour = 1000
            }));

            SetupPerformance(performance);

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.Equal(1.85, result.ArbitrageProfitEur, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfSufficiencyPctCappedAt100()
        {
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 0, ConsumedTariff2 = 0, ProducedTariff1 = 0, ProducedTariff2 = 0 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 0, ConsumedTariff2 = 0, ProducedTariff1 = 5, ProducedTariff2 = 5 }
            });
            SetupPerformance(BuildPerformanceWithSolar(20.0));

            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            Assert.True(result.SelfSufficiencyPct <= 100.0);
            Assert.True(result.SelfSufficiencyPct >= 0.0);
        }

        // ── Investment statistics tests ──────────────────────────────────────

        [Fact]
        public async Task GetInvestmentStatistics_CalculatesNetInvestmentCorrectly()
        {
            SetupInvestments(new List<Investment>
            {
                new() { Category = "SolarPanels", AmountEur = 10000, SubsidyEur = 2000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 25 },
                new() { Category = "Battery",     AmountEur = 8000,  SubsidyEur = 0,    PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 15 },
                new() { Category = "HeatPump",    AmountEur = 6000,  SubsidyEur = 1000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 20 }
            });
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetInvestmentStatisticsAsync();

            Assert.Equal(21000.0, result.TotalNetInvestmentEur, 2);
        }

        [Fact]
        public async Task GetInvestmentStatistics_CategoryBreakdownCorrect()
        {
            SetupInvestments(new List<Investment>
            {
                new() { Category = "SolarPanels", AmountEur = 10000, SubsidyEur = 2000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 25 },
                new() { Category = "Battery",     AmountEur = 8000,  SubsidyEur = 0,    PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 15 }
            });
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetInvestmentStatisticsAsync();

            Assert.Equal(2, result.CategoryBreakdown.Count);

            var solar = result.CategoryBreakdown.First(c => c.Category == "SolarPanels");
            Assert.Equal(8000.0, solar.NetAmountEur, 2);
            Assert.Equal(320.0, solar.AnnualDepreciationEur, 2);

            var battery = result.CategoryBreakdown.First(c => c.Category == "Battery");
            Assert.Equal(8000.0, battery.NetAmountEur, 2);
        }

        [Fact]
        public async Task GetInvestmentStatistics_ReturnsEmptyWhenNoInvestments()
        {
            SetupInvestments(new List<Investment>());
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetInvestmentStatisticsAsync();

            Assert.Equal(0.0, result.TotalNetInvestmentEur);
            Assert.Empty(result.CategoryBreakdown);
        }

        // ── Monthly trend tests ──────────────────────────────────────────────

        [Fact]
        public async Task GetMonthlyTrends_ReturnsCorrectNumberOfMonths()
        {
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            var result = await _sut.GetMonthlyTrendsAsync(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 5, 31));

            Assert.Equal(5, result.Count);
        }

        [Fact]
        public async Task GetMonthlyTrends_MonthsAreCorrectlyLabeled()
        {
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

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
            // Netting on: 8 cheap quarters (0.05) then 8 expensive quarters (0.30).
            // Price spread (0.25) >> cycle cost (0.02) → solver should charge then discharge.
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints =
                Enumerable.Range(0, 8).Select(i => new PricePoint(
                    baseTime.AddMinutes(i * 15),
                    BuyEurPerKWh: 0.05, SellEurPerKWh: 0.05, NetLoadWh: 0,
                    SelfUseValueEurPerKWh: 0.05))
                .Concat(Enumerable.Range(8, 8).Select(i => new PricePoint(
                    baseTime.AddMinutes(i * 15),
                    BuyEurPerKWh: 0.30, SellEurPerKWh: 0.30, NetLoadWh: 0,
                    SelfUseValueEurPerKWh: 0.30)))
                .ToList();

            var spec = MakeSpec(initialSocKWh: 4.0);
            var opt = MakeOpt(cycleCost: 0.02);

            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

            Assert.True(result.Plan.Count > 0);
            Assert.True(result.Plan.Any(p => p.Mode == ActionMode.Charge),
                "Expected charging during cheap quarters");
            Assert.True(result.Plan.Any(p => p.Mode == ActionMode.Discharge),
                "Expected discharging during expensive quarters");
        }

        [Fact]
        public void BatteryArbitrageMilp_NettingOff_SelfUseValueDrivesDischarge()
        {
            // Netting off: sell price very low (0.03) but self-use value high (0.25).
            // Solver should plan discharge because max(sell, selfUse) = 0.25 > buy + cycleCost.
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints = new List<PricePoint>
            {
                new(baseTime,                BuyEurPerKWh: 0.05, SellEurPerKWh: 0.03, NetLoadWh: 500,  SelfUseValueEurPerKWh: 0.25),
                new(baseTime.AddMinutes(15), BuyEurPerKWh: 0.05, SellEurPerKWh: 0.03, NetLoadWh: 500,  SelfUseValueEurPerKWh: 0.25),
                new(baseTime.AddMinutes(30), BuyEurPerKWh: 0.25, SellEurPerKWh: 0.03, NetLoadWh: 2000, SelfUseValueEurPerKWh: 0.25),
                new(baseTime.AddMinutes(45), BuyEurPerKWh: 0.25, SellEurPerKWh: 0.03, NetLoadWh: 2000, SelfUseValueEurPerKWh: 0.25),
            };

            var spec = MakeSpec(initialSocKWh: 8.0);
            var opt = MakeOpt(cycleCost: 0.02);

            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

            Assert.True(result.Plan.Count > 0);
            Assert.True(result.Plan.Any(p => p.Mode == ActionMode.Discharge),
                "Expected discharge during high-consumption quarters with high self-use value");
        }

        [Fact]
        public void BatteryArbitrageMilp_NettingOff_DoesNotChargeWhenCyclingNotProfitable()
        {
            // Charging cost = buy + cycleCost = 0.03 + 0.20 = 0.23 EUR/kWh.
            // Discharge value = max(sell, selfUse) = 0.03 EUR/kWh.
            // Net cycle profit = 0.03 - 0.23 = -0.20 EUR/kWh → charging is deeply unprofitable.
            // Starting with empty battery (initialSoc=0) ensures no free energy to discharge,
            // so the solver has no incentive to do anything.
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints = Enumerable.Range(0, 4).Select(i => new PricePoint(
                baseTime.AddMinutes(i * 15),
                BuyEurPerKWh: 0.03,
                SellEurPerKWh: 0.03,
                NetLoadWh: 0,
                SelfUseValueEurPerKWh: 0.03
            )).ToList();

            // Empty battery: no free energy to discharge, charging unprofitable.
            var spec = new BatterySpec(
                CapacityKWh: 16.2, InitialSocKWh: 0.0,
                MinSocKWh: 0.0, MaxSocKWh: 16.2,
                MaxChargeKW: 5.4, MaxDischargeKW: 5.1,
                ChargeEfficiency: 0.95, DischargeEfficiency: 0.95);
            var opt = MakeOpt(cycleCost: 0.20);

            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

            Assert.True(result.Plan.Count > 0);
            Assert.False(result.Plan.Any(p => p.Mode == ActionMode.Charge),
                "Expected no charging when cycle cost makes it unprofitable");
            Assert.False(result.Plan.Any(p => p.Mode == ActionMode.Discharge),
                "Expected no discharging when battery starts empty");
        }

        [Fact]
        public void BatteryArbitrageMilp_SelfUseValueDefaultsToZero_WhenNotProvided()
        {
            // When SelfUseValueEurPerKWh is omitted (default 0.0), solver falls back
            // to sell price only — existing netting-on behavior is preserved.
            var baseTime = new DateTime(2027, 1, 1);
            var pricePoints = new List<PricePoint>
            {
                new(baseTime,                BuyEurPerKWh: 0.05, SellEurPerKWh: 0.25, NetLoadWh: 0),
                new(baseTime.AddMinutes(15), BuyEurPerKWh: 0.05, SellEurPerKWh: 0.25, NetLoadWh: 0),
            };

            var spec = MakeSpec(initialSocKWh: 8.0);
            var opt = MakeOpt(cycleCost: 0.02);

            // Should not throw — default value for SelfUseValueEurPerKWh is 0.0.
            var result = BatteryArbitrageMilp.Solve(pricePoints, spec, opt);

            Assert.NotNull(result);
        }

        // ── Helper methods ───────────────────────────────────────────────────

        private void SetupEnergyHistory(List<EnergyHistory> data)
        {
            _energyHistoryMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<EnergyHistory>, Task<List<EnergyHistory>>>>()))
                .ReturnsAsync(data);
        }

        private void SetupPerformance(List<Performance> data)
        {
            _performanceMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<Performance>, Task<List<Performance>>>>()))
                .ReturnsAsync(data);
        }

        private void SetupInvestments(List<Investment> data)
        {
            _investmentMock
                .Setup(s => s.GetList(It.IsAny<Func<IQueryable<Investment>, Task<List<Investment>>>>()))
                .ReturnsAsync(data);
        }

        private static List<Performance> BuildPerformanceWithSolar(double totalKWh)
        {
            double perQuarter = totalKWh / 10.0;
            double consumptionW = perQuarter / 0.25 * 1000.0;

            return Enumerable.Range(0, 10)
                .Select(i => new Performance
                {
                    Time = PeriodStart.AddMinutes(i * 15),
                    SolarPowerPerQuarterHour = perQuarter,
                    EstimatedConsumptionPerQuarterHour = consumptionW
                })
                .ToList();
        }

        private static BatterySpec MakeSpec(double initialSocKWh) => new BatterySpec(
            CapacityKWh: 16.2, InitialSocKWh: initialSocKWh,
            MinSocKWh: 0.0, MaxSocKWh: 16.2,
            MaxChargeKW: 5.4, MaxDischargeKW: 5.1,
            ChargeEfficiency: 0.95, DischargeEfficiency: 0.95);

        private static SessyOptions MakeOpt(double cycleCost) => new SessyOptions(
            QuarterMinutes: 15, ActiveQuarterPenaltyEur: 0.0,
            ForbidSimultaneousChargeDischarge: true, TimeLimitMs: 5000,
            CycleCostEurPerKWh: cycleCost);
    }
}
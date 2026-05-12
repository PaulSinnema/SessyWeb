using Microsoft.Extensions.Options;
using Moq;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services;
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
            _energyHistoryMock = new Mock<EnergyHistoryDataService>(MockBehavior.Loose, null!);
            _performanceMock = new Mock<PerformanceDataService>(MockBehavior.Loose, null!);
            _investmentMock = new Mock<InvestmentDataService>(MockBehavior.Loose, null!);
            _timeZoneMock = new Mock<TimeZoneService>(MockBehavior.Loose);

            _timeZoneMock.Setup(t => t.Now).Returns(new DateTime(2026, 5, 31, 12, 0, 0));

            var heatPumpConfig = Options.Create(new HeatPumpConfig
            {
                AnnualGasConsumptionM3 = 950,
                GasPriceEurPerM3 = 1.45,
                GasStandingChargeEurPerYear = 185.0,
                InstallationDate = new DateTime(2024, 3, 1)
            });

            _sut = new EnergyStatisticsService(
                _energyHistoryMock.Object,
                _performanceMock.Object,
                _investmentMock.Object,
                _timeZoneMock.Object,
                heatPumpConfig);
        }

        // ── Grid flow tests ──────────────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesGridImportCorrectly()
        {
            // Arrange
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 1000, ConsumedTariff2 = 500, ProducedTariff1 = 100, ProducedTariff2 = 50 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 1150, ConsumedTariff2 = 600, ProducedTariff1 = 200, ProducedTariff2 = 80 }
            });
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(250.0, result.TotalGridImportKWh, 2); // (150 + 100)
            Assert.Equal(130.0, result.TotalGridExportKWh, 2); // (100 + 30)
        }

        [Fact]
        public async Task GetEnergyStatistics_ClampsNegativeGridFlowsToZero()
        {
            // Arrange — meter reset scenario
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 1000, ConsumedTariff2 = 500, ProducedTariff1 = 200, ProducedTariff2 = 80 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 900,  ConsumedTariff2 = 400, ProducedTariff1 = 100, ProducedTariff2 = 50 }
            });
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(0.0, result.TotalGridImportKWh);
            Assert.Equal(0.0, result.TotalGridExportKWh);
        }

        [Fact]
        public async Task GetEnergyStatistics_ReturnsZeroWhenNoHistory()
        {
            // Arrange
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(0.0, result.TotalGridImportKWh);
            Assert.Equal(0.0, result.TotalGridExportKWh);
        }

        // ── Solar statistics tests ───────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesTotalSolarProductionCorrectly()
        {
            // Arrange — 4 quarters each producing 1 kWh = 4 kWh total
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                    SolarPowerPerQuarterHour = 1.0 },
                new() { Time = PeriodStart.AddMinutes(15),     SolarPowerPerQuarterHour = 1.0 },
                new() { Time = PeriodStart.AddMinutes(30),     SolarPowerPerQuarterHour = 1.0 },
                new() { Time = PeriodStart.AddMinutes(45),     SolarPowerPerQuarterHour = 1.0 }
            });

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(4.0, result.TotalSolarProductionKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesSelfConsumedSolarCorrectly()
        {
            // Arrange — 10 kWh solar, 3 kWh exported → 7 kWh self-consumed
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 0, ConsumedTariff2 = 0, ProducedTariff1 = 0,  ProducedTariff2 = 0 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 5, ConsumedTariff2 = 0, ProducedTariff1 = 2,  ProducedTariff2 = 1 }
            });
            SetupPerformance(BuildPerformanceWithSolar(10.0));

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(10.0, result.TotalSolarProductionKWh, 1);
            Assert.Equal(7.0, result.SelfConsumedSolarKWh, 1); // 10 - 3 export
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfConsumptionPctCorrect()
        {
            // Arrange — 10 kWh solar, 5 kWh exported → 50% self-consumption
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 0, ConsumedTariff2 = 0, ProducedTariff1 = 0, ProducedTariff2 = 0 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 2, ConsumedTariff2 = 0, ProducedTariff1 = 3, ProducedTariff2 = 2 }
            });
            SetupPerformance(BuildPerformanceWithSolar(10.0));

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(50.0, result.SelfConsumptionPct, 0);
        }

        // ── Battery statistics tests ─────────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryChargedCorrectly()
        {
            // Arrange — 4 charging quarters at 1800W = 4 * 1800 * 0.25 / 1000 = 1.8 kWh
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>
            {
                new() { Time = PeriodStart,                Charging = true,  EstimatedConsumptionPerQuarterHour = 1800 },
                new() { Time = PeriodStart.AddMinutes(15), Charging = true,  EstimatedConsumptionPerQuarterHour = 1800 },
                new() { Time = PeriodStart.AddMinutes(30), Charging = true,  EstimatedConsumptionPerQuarterHour = 1800 },
                new() { Time = PeriodStart.AddMinutes(45), Charging = true,  EstimatedConsumptionPerQuarterHour = 1800 }
            });

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(1.8, result.TotalBatteryChargedKWh, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesBatteryCyclesCorrectly()
        {
            // Arrange — charge 16.2 kWh (= 1 full cycle for 3x Sessy)
            var performance = Enumerable.Range(0, 36) // 36 quarters * 1800W * 0.25h / 1000 = 16.2 kWh
                .Select(i => new Performance
                {
                    Time = PeriodStart.AddMinutes(i * 15),
                    Charging = true,
                    EstimatedConsumptionPerQuarterHour = 1800
                })
                .ToList();

            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(performance);

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert — should be approximately 1 cycle
            Assert.Equal(1.0, result.BatteryCycles, 1);
        }

        [Fact]
        public async Task GetEnergyStatistics_CalculatesRoundTripEfficiencyCorrectly()
        {
            // Arrange — charge 10 kWh, discharge 9.5 kWh → 95% efficiency
            SetupEnergyHistory(new List<EnergyHistory>());

            var performance = new List<Performance>();

            // 40 quarters charging at 1000W = 10 kWh
            performance.AddRange(Enumerable.Range(0, 40).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Charging = true,
                EstimatedConsumptionPerQuarterHour = 1000
            }));

            // 38 quarters discharging at 1000W = 9.5 kWh
            performance.AddRange(Enumerable.Range(40, 38).Select(i => new Performance
            {
                Time = PeriodStart.AddMinutes(i * 15),
                Discharging = true,
                EstimatedConsumptionPerQuarterHour = 1000
            }));

            SetupPerformance(performance);

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(95.0, result.BatteryRoundTripEfficiencyPct, 0);
        }

        // ── Financial statistics tests ───────────────────────────────────────

        [Fact]
        public async Task GetEnergyStatistics_CalculatesArbitrageProfitCorrectly()
        {
            // Arrange — buy 10 kWh at 0.10 EUR, sell 9.5 kWh at 0.30 EUR
            // Arbitrage = 9.5 * 0.30 - 10 * 0.10 = 2.85 - 1.00 = 1.85 EUR
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

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.Equal(1.85, result.ArbitrageProfitEur, 2);
        }

        [Fact]
        public async Task GetEnergyStatistics_SelfSufficiencyPctCappedAt100()
        {
            // Arrange — more solar than consumption
            SetupEnergyHistory(new List<EnergyHistory>
            {
                new() { Time = PeriodStart, ConsumedTariff1 = 0, ConsumedTariff2 = 0, ProducedTariff1 = 0, ProducedTariff2 = 0 },
                new() { Time = PeriodEnd,   ConsumedTariff1 = 0, ConsumedTariff2 = 0, ProducedTariff1 = 5, ProducedTariff2 = 5 }
            });
            SetupPerformance(BuildPerformanceWithSolar(20.0));

            // Act
            var result = await _sut.GetEnergyStatisticsAsync(PeriodStart, PeriodEnd);

            // Assert
            Assert.True(result.SelfSufficiencyPct <= 100.0);
        }

        // ── Investment statistics tests ──────────────────────────────────────

        [Fact]
        public async Task GetInvestmentStatistics_CalculatesNetInvestmentCorrectly()
        {
            // Arrange
            SetupInvestments(new List<Investment>
            {
                new() { Category = "SolarPanels", AmountEur = 10000, SubsidyEur = 2000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 25 },
                new() { Category = "Battery",     AmountEur = 8000,  SubsidyEur = 0,    PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 15 },
                new() { Category = "HeatPump",    AmountEur = 6000,  SubsidyEur = 1000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 20 }
            });
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetInvestmentStatisticsAsync();

            // Assert
            Assert.Equal(21000.0, result.TotalNetInvestmentEur, 2); // 8000 + 8000 + 5000
        }

        [Fact]
        public async Task GetInvestmentStatistics_CategoryBreakdownCorrect()
        {
            // Arrange
            SetupInvestments(new List<Investment>
            {
                new() { Category = "SolarPanels", AmountEur = 10000, SubsidyEur = 2000, PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 25 },
                new() { Category = "Battery",     AmountEur = 8000,  SubsidyEur = 0,    PurchaseDate = new DateTime(2025, 1, 1), ExpectedLifetimeYears = 15 }
            });
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetInvestmentStatisticsAsync();

            // Assert
            Assert.Equal(2, result.CategoryBreakdown.Count);

            var solar = result.CategoryBreakdown.First(c => c.Category == "SolarPanels");
            Assert.Equal(8000.0, solar.NetAmountEur, 2);
            Assert.Equal(320.0, solar.AnnualDepreciationEur, 2); // 8000 / 25

            var battery = result.CategoryBreakdown.First(c => c.Category == "Battery");
            Assert.Equal(8000.0, battery.NetAmountEur, 2);
        }

        [Fact]
        public async Task GetInvestmentStatistics_ReturnsEmptyWhenNoInvestments()
        {
            // Arrange
            SetupInvestments(new List<Investment>());
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetInvestmentStatisticsAsync();

            // Assert
            Assert.Equal(0.0, result.TotalNetInvestmentEur);
            Assert.Empty(result.CategoryBreakdown);
        }

        // ── Monthly trend tests ──────────────────────────────────────────────

        [Fact]
        public async Task GetMonthlyTrends_ReturnsCorrectNumberOfMonths()
        {
            // Arrange
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetMonthlyTrendsAsync(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 5, 31));

            // Assert
            Assert.Equal(5, result.Count);
        }

        [Fact]
        public async Task GetMonthlyTrends_MonthsAreCorrectlyLabeled()
        {
            // Arrange
            SetupEnergyHistory(new List<EnergyHistory>());
            SetupPerformance(new List<Performance>());

            // Act
            var result = await _sut.GetMonthlyTrendsAsync(
                new DateTime(2026, 3, 1),
                new DateTime(2026, 5, 31));

            // Assert
            Assert.Equal(3, result[0].Month);
            Assert.Equal(4, result[1].Month);
            Assert.Equal(5, result[2].Month);
        }

        // ── Derived metric tests ─────────────────────────────────────────────

        [Fact]
        public void EnergyStatistics_MonthlySavingsExtrapolatedCorrectly()
        {
            // Arrange — 30 days, 60 EUR savings → 60 EUR/month
            var stats = new EnergyStatistics
            {
                PeriodStart = new DateTime(2026, 5, 1),
                PeriodEnd = new DateTime(2026, 5, 31),
                ActualEnergyCostEur = 40,
                BaselineEnergyCostEur = 100
            };

            // Assert
            Assert.Equal(60.0, stats.TotalSavingsEur, 2);
            Assert.Equal(60.0, stats.MonthlySavingsEur, 0);
            Assert.Equal(730.0, stats.AnnualSavingsEur, 0);
        }

        [Fact]
        public void EnergyStatistics_GridDependencyPctCorrect()
        {
            // Arrange — 100 kWh consumed, 30 kWh from grid → 30% grid dependency
            var stats = new EnergyStatistics
            {
                PeriodStart = PeriodStart,
                PeriodEnd = PeriodEnd,
                TotalConsumptionKWh = 100,
                TotalGridImportKWh = 30
            };

            // Assert
            Assert.Equal(30.0, stats.GridDependencyPct, 1);
        }

        [Fact]
        public void InvestmentStatistics_ProjectedPaybackYearsCorrect()
        {
            // Arrange — 21000 EUR investment, 3000 EUR/year savings → 7 years
            var stats = new InvestmentStatistics
            {
                TotalNetInvestmentEur = 21000,
                TotalRealizedSavingsEur = 0,
                ProjectedAnnualSavingsEur = 3000
            };

            // Assert
            Assert.Equal(7.0, stats.ProjectedPaybackYears, 1);
        }

        [Fact]
        public void InvestmentStatistics_RecoveredPctCorrect()
        {
            // Arrange — 21000 EUR investment, 7000 EUR recovered → 33.3%
            var stats = new InvestmentStatistics
            {
                TotalNetInvestmentEur = 21000,
                TotalRealizedSavingsEur = 7000,
                ProjectedAnnualSavingsEur = 3000
            };

            // Assert
            Assert.Equal(33.3, stats.RecoveredPct, 0);
            Assert.Equal(14000.0, stats.RemainingInvestmentEur, 1);
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
            // Spread solar over 10 quarters.
            double perQuarter = totalKWh / 10.0;

            return Enumerable.Range(0, 10)
                .Select(i => new Performance
                {
                    Time = PeriodStart.AddMinutes(i * 15),
                    SolarPowerPerQuarterHour = perQuarter
                })
                .ToList();
        }
    }
}
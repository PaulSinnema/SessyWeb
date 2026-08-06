using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization.Strategies;
using SessyData.Services;

namespace SessyController.Services
{
    public sealed class BalancedMilpService : StrategyMilpService
    {
        public BalancedMilpService(
            LoggingService<MilpServiceBase> logger,
            SettingsService settingsService,
            IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
            BatteryContainer batteryContainer,
            TimeZoneService timeZoneService,
            TaxesDataService taxesDataService,
            PlannedActionDataService plannedActionDataService,
            PlannedQuarterDataService plannedQuarterDataService,
            ForecastSnapshotDataService forecastSnapshotDataService,
            ChargeCostBasisService chargeCostBasisService,
            ReplacementCostService replacementCostService,
            ThrottleAnalysisService throttleAnalysisService,
            WeatherService weatherService,
            BatteryEfficiencyService batteryEfficiencyService,
            ControlModeService controlMode)
            : base(new BalancedStrategy(), logger, settingsService, sessyBatteryConfigMonitor,
                   batteryContainer, timeZoneService, taxesDataService,
                   plannedActionDataService, plannedQuarterDataService, forecastSnapshotDataService,
                   chargeCostBasisService, replacementCostService,
                   throttleAnalysisService, weatherService, batteryEfficiencyService, controlMode)
        {
        }
    }
}
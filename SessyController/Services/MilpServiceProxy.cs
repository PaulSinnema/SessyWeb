using SessyCommon.Enums;
using SessyController.Interfaces;
using SessyController.Services.Items;
using SessyController.Services.Statistics;
using SessyData.Model;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services
{
    /// <summary>
    /// Proxy for IMilpService that delegates to the correct strategy implementation
    /// based on the current Settings.Strategy value.
    /// This allows strategy changes via the UI to take effect without restarting.
    /// </summary>
    public sealed class MilpServiceProxy : IMilpService
    {
        private readonly IServiceProvider _sp;
        private readonly SettingsService _settings;

        public MilpServiceProxy(IServiceProvider sp, SettingsService settings)
        {
            _sp = sp;
            _settings = settings;
        }

        private IMilpService Current => _settings.Current.Strategy switch
        {
            OptimizationStrategy.SelfConsumption => _sp.GetRequiredService<SelfConsumptionMilpService>(),
            OptimizationStrategy.Balanced => _sp.GetRequiredService<BalancedMilpService>(),
            OptimizationStrategy.BatterySaving => _sp.GetRequiredService<BatterySavingMilpService>(),
            OptimizationStrategy.ProfitMaximization => _sp.GetRequiredService<ProfitMaximizationMilpService>(),
            _ => throw new InvalidOperationException($"Wrong strategy {_settings.Current.Strategy}")
        };

        public Task BuildPlanAsync(List<QuarterlyInfo> quarterlyInfos, double currentSocWh)
            => Current.BuildPlanAsync(quarterlyInfos, currentSocWh);

        public Task<(Modes Mode, double PowerW)> GetExecutableActionForNowAsync(DateTime nowQuarter)
            => Current.GetExecutableActionForNowAsync(nowQuarter);

        public Task<PlanStatistics> GetPlanStatisticsAsync(DateTime now, double currentSocWh)
            => Current.GetPlanStatisticsAsync(now, currentSocWh);

        public Task ClearPlanAsync()
            => Current.ClearPlanAsync();

        public Task<bool> TryRestorePlanAsync()
            => Current.TryRestorePlanAsync();

        public bool HasPlanFor(DateTime quarter)
    => Current.HasPlanFor(quarter);

        public void Dispose()
            => Current.Dispose();
    }
}
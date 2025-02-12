using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Sessy;

namespace SessyController.Services
{
    public class SessyMonitorService : BackgroundService
    {
        private LoggingService<SessyMonitorService> _logger;
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;
        private IServiceScopeFactory _serviceScopeFactory;
        private SessyStatusHistoryService _sessyStatusHistoryService;
        private SettingsConfig _settingsConfig;
        private SessyBatteryConfig _sessyBatteryConfig;
        private SessyService _sessyService;
        private BatteryContainer _batteryContainer;

        public SessyMonitorService(LoggingService<SessyMonitorService> logger,
                                  IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                  IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
                                  IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _settingsConfigMonitor = settingsConfigMonitor;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;
            _serviceScopeFactory = serviceScopeFactory;

            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _sessyBatteryConfig = _sessyBatteryConfigMonitor.CurrentValue;

            using (var scope = _serviceScopeFactory.CreateScope())
            {
                _sessyService = scope.ServiceProvider.GetRequiredService<SessyService>();
                _batteryContainer = scope.ServiceProvider.GetRequiredService<BatteryContainer>();
                _sessyStatusHistoryService = scope.ServiceProvider.GetRequiredService<SessyStatusHistoryService>();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while monitoring batteries.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
            }
        }

        public async Task Process(CancellationToken cancellationToken)
        {
            foreach (var battery in _batteryContainer.Batteries)
            {
                var powerStatus = await battery.GetPowerStatus();

                var status = powerStatus.Sessy.SystemStateString;

                var errorState = SystemStates.SYSTEM_STATE_ERROR.ToString().ToLower();

                if (status.ToLower() == errorState)
                {
                    StoreStatus(battery, powerStatus);
                }
            }
        }

        private void StoreStatus(Battery battery, PowerStatus powerStatus)
        {
            var statusList = new List<SessyStatusHistory>();

            statusList.Add(new SessyStatusHistory
            {
                Name = battery.Id,
                Status = powerStatus.Sessy?.SystemStateString,
                StatusDetails = powerStatus.Sessy?.SystemStateDetails
            });

            _sessyStatusHistoryService.StoreSessyStatusHistoryList(statusList);
        }
    }
}

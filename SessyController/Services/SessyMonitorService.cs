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
        private LoggingService<SessyMonitorService> _logger { get; set; }
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor { get; set; }
        private IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private IServiceScopeFactory _serviceScopeFactory { get; set; }
        private SessyStatusHistoryService _sessyStatusHistoryService { get; set; }
        private SettingsConfig _settingsConfig { get; set; }
        private SessyBatteryConfig _sessyBatteryConfig { get; set; }
        private IServiceScope _scope { get; set; }
        private SessyService _sessyService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }

        public SessyMonitorService(LoggingService<SessyMonitorService> logger,
                                  IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                  IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
                                  TimeZoneService timeZoneService,
                                  IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _settingsConfigMonitor = settingsConfigMonitor;
            _sessyBatteryConfigMonitor = sessyBatteryConfigMonitor;
            _timeZoneService = timeZoneService;
            _serviceScopeFactory = serviceScopeFactory;

            _settingsConfig = settingsConfigMonitor.CurrentValue;
            _sessyBatteryConfig = _sessyBatteryConfigMonitor.CurrentValue;

            _scope = _serviceScopeFactory.CreateScope();
            
                _sessyService = _scope.ServiceProvider.GetRequiredService<SessyService>();
                _batteryContainer = _scope.ServiceProvider.GetRequiredService<BatteryContainer>();
                _sessyStatusHistoryService = _scope.ServiceProvider.GetRequiredService<SessyStatusHistoryService>();
            
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
                Time = _timeZoneService.Now,
                Name = battery.Id,
                Status = powerStatus.Sessy?.SystemStateString,
                StatusDetails = powerStatus.Sessy?.SystemStateDetails
            });

            _sessyStatusHistoryService.Store(statusList);
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if(!_isDisposed)
            {
                _scope.Dispose();

                base.Dispose();
            }
        }
    }
}

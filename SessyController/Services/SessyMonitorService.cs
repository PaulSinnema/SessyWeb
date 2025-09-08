using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

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
            _logger.LogWarning("Sessy monitor service started ...");

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
                    await Task.Delay(TimeSpan.FromSeconds(10), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
            }

            _logger.LogWarning("Sessy monitor service started ...");
        }

        public async Task Process(CancellationToken cancellationToken)
        {
            foreach (var battery in _batteryContainer.Batteries)
            {
                var powerStatus = await battery.GetPowerStatus();

                var status = powerStatus.Sessy.SystemState;

                MonitorStatus(battery, powerStatus);
            }
        }

        private Dictionary<string, PreviousStatus> StatusList = new Dictionary<string, PreviousStatus>();

        private void MonitorStatus(Battery battery, PowerStatus powerStatus)
        {
            if(!StatusList.ContainsKey(battery.Id))
            {
                StatusList.Add(battery.Id, new PreviousStatus(_timeZoneService, _sessyStatusHistoryService, battery, powerStatus));
            }

            StatusList[battery.Id].PowerStatus = powerStatus;
        }

        private class PreviousStatus
        {
            public PreviousStatus(TimeZoneService timeZoneService,
                                   SessyStatusHistoryService sessyStatusHistoryService,
                                   Battery battery, 
                                   PowerStatus powerStatus)
            {
                _timeZoneService = timeZoneService;
                _sessyStatusHistoryService = sessyStatusHistoryService;
                Battery = battery;
                PowerStatus = powerStatus;
            }

            private PowerStatus? _powerStatus;
            private TimeZoneService _timeZoneService { get; set; }
            private SessyStatusHistoryService _sessyStatusHistoryService { get; set; }

            public Battery? Battery { get; set; }

            public PowerStatus? PowerStatus
            {
                get => _powerStatus;
                set
                {
                    if(_powerStatus == null ||
                       _powerStatus.Sessy.SystemState != value.Sessy.SystemState ||
                       _powerStatus.Sessy.SystemStateDetails != value.Sessy.SystemStateDetails)
                    {
                        _powerStatus = value;

                        var task = StoreStatus(Battery!, PowerStatus!);

                        if(task != null)
                        {
                            Task.WhenAll(task);
                        }
                    }
                }
            }
            private async Task StoreStatus(Battery battery, PowerStatus powerStatus)
            {
                var statusList = new List<SessyStatusHistory>();

                statusList.Add(new SessyStatusHistory
                {
                    Time = _timeZoneService.Now,
                    Name = battery.Id,
                    Status = powerStatus.Sessy?.SystemStateString,
                    StatusDetails = powerStatus.Sessy?.SystemStateDetails
                });

                await _sessyStatusHistoryService.AddRange(statusList);
            }
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _scope.Dispose();

                base.Dispose();
            }
        }
    }
}

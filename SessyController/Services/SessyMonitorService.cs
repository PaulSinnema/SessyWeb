﻿using Microsoft.Extensions.Options;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using static SessyController.Services.Sessy;

namespace SessyController.Services
{
    public class SessyMonitorService : BackgroundService
    {
        private LoggingService<SessyMonitorService> _logger;
        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;
        private IServiceScopeFactory _serviceScopeFactory;
        private SettingsConfig _settingsConfig;
        private SessyBatteryConfig _sessyBatteryConfig;
        private SessyService _sessyService;
        private BatteryContainer _batteryContainer;

        public SessyMonitorService(LoggingService<SessyMonitorService> logger,
                                IOptionsMonitor<SettingsConfig> settingsConfigMonitor,
                                IOptionsMonitor<SessyBatteryConfig> sessyBatteryConfigMonitor,
                                ModelContext modelContext,
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
                    await Task.Delay(TimeSpan.FromSeconds(60), cancelationToken);
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

                var status = powerStatus.Status;

                if(status == SystemStates.SYSTEM_STATE_ERROR.ToString())
                {

                }
            }
        }
    }
}

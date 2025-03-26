﻿using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using static P1MeterService;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    public class EnergyMonitorService : BackgroundService
    {
        private P1MeterContainer _p1MeterContainer { get; set; }
        private IOptionsMonitor<SessyP1Config> _sessyP1ConfigMonitor { get; set; }
        private WeatherService _weatherService { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private P1MeterService _p1MeterService { get; set; }

        private DayAheadMarketService _dayAheadMarketService { get; set; }

        private EnergyHistoryService _energyHistoryService { get; set; }

        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private IServiceScope _scope;

        private LoggingService<EnergyMonitorService> _logger { get; set; }

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        public EnergyMonitorService(LoggingService<EnergyMonitorService> logger,
                                    WeatherService weatherService,
                                    TimeZoneService timeZoneService,
                                    IOptionsMonitor<SessyP1Config> sessyP1ConfigMonitor,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _sessyP1ConfigMonitor = sessyP1ConfigMonitor;
            _weatherService = weatherService;
            _timeZoneService = timeZoneService;
            _serviceScopeFactory = serviceScopeFactory;

            _scope = _serviceScopeFactory.CreateScope();

            _energyHistoryService = _scope.ServiceProvider.GetRequiredService<EnergyHistoryService>();
            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _dayAheadMarketService = _scope.ServiceProvider.GetRequiredService<DayAheadMarketService>();

            _p1MeterContainer = new P1MeterContainer(_sessyP1ConfigMonitor, _p1MeterService);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _scope.Dispose();

            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Energy monitor service started ...");

            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while monitoring p1 meters.");
                }

                try
                {
                    var now = _timeZoneService.Now;
                    var delayTime = 60 - now.Minute;

                    await Task.Delay(TimeSpan.FromMinutes(delayTime), cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred during delay of thread.");
                    // Ignore exception during delay
                }
            }

            _logger.LogWarning("Energy monitor service stopped");
        }

        private async Task Process(CancellationToken cancelationToken)
        {
            await EnsureServicesAreInitialized(cancelationToken);

            foreach (P1Meter? p1Meter in _p1MeterContainer.P1Meters)
            {
                var now = _timeZoneService.Now;
                var selectTime = now.DateHour();

                if (GetEnergyHistory(selectTime) == null)
                {
                    var p1Details = await _p1MeterContainer.GetDetails(p1Meter.Id!);
                    var weatherData = _weatherService.GetWeatherData();
                    var prices = _dayAheadMarketService.GetPrices();

                    var weatherHourData = weatherData.UurVerwachting
                        .FirstOrDefault(uv => uv.TimeStamp == selectTime);
                    var hourlyInfo = prices
                        .FirstOrDefault(hi => hi.Time.DateHour() == selectTime);

                    if (weatherHourData != null)
                    {
                        StoreEnergyHistory(p1Meter, p1Details!, hourlyInfo, selectTime, weatherHourData);

                        DataChanged?.Invoke();
                    }
                }
            }
        }

        private async Task EnsureServicesAreInitialized(CancellationToken cancelationToken)
        {
            var tries = 0;

            while (!_weatherService.Initialized && tries++ < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken);
            }

            tries = 0;

            while (!_dayAheadMarketService.PricesInitialized && tries++ < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken);
            }

            if (!(_dayAheadMarketService.PricesInitialized && _weatherService.Initialized))
                throw new InvalidOperationException("Day ahead service or weather service not initialized");
        }

        private EnergyHistory? GetEnergyHistory(DateTime selectTime)
        {
            var energyHistory = _energyHistoryService.Get((set) =>
            {
                return set
                    .Where(eh => eh.Time == selectTime)
                    .FirstOrDefault();
            });

            return energyHistory;
        }

        private void StoreEnergyHistory(P1Meter p1Meter, P1Details p1Details, HourlyInfo? hourlyInfo, DateTime time, UurVerwachting hourExpectancy)
        {
            var energyHistoryList = new List<EnergyHistory>();

            energyHistoryList.Add(new EnergyHistory
            {
                Time = time,
                MeterId = p1Meter.Id,
                ConsumedTariff1 = p1Details.PowerConsumedTariff1,
                ConsumedTariff2 = p1Details.PowerConsumedTariff2,
                ProducedTariff1 = p1Details.PowerProducedTariff1,
                ProducedTariff2 = p1Details.PowerProducedTariff2,
                TarrifIndicator = p1Details.TariffIndicator,
                Temperature = hourExpectancy.Temp,
                GlobalRadiation = hourExpectancy.GlobalRadiation,
                Price = hourlyInfo != null ? hourlyInfo.Price : 0.0
            });

            _energyHistoryService.AddRange(energyHistoryList);
        }
    }
}


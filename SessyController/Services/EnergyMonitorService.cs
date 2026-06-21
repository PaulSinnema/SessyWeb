using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using static P1MeterService;
using static SessyController.Services.WeatherService;

namespace SessyController.Services
{
    /// <summary>
    /// Monitors the P1 meter each quarter-hour and stores grid import/export deltas
    /// plus weather data into QuarterlyMeasurement records.
    /// Battery and solar data are filled in by BatteriesService.StoreQuarterlyMeasurement().
    /// </summary>
    public class EnergyMonitorService : BackgroundService
    {
        public WeatherService? _weatherService { get; set; }
        public EPEXPricesService? _epexPricesService { get; set; }
        private P1MeterContainer _p1MeterContainer { get; set; }
        private TimeZoneService _timeZoneService { get; set; }
        private P1MeterService _p1MeterService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }

        private QuarterlyMeasurementDataService _measurementService { get; set; }
        private EnergyHistoryDataService _energyHistoryService { get; set; }

        private IServiceScopeFactory _serviceScopeFactory { get; set; }

        private IServiceScope _scope;

        private LoggingService<EnergyMonitorService> _logger { get; set; }

        // Cache the previous P1 reading to calculate deltas.
        private P1Details? _previousP1Details { get; set; }
        private DateTime? _previousP1Time { get; set; }

        public delegate Task DataChangedDelegate();

        public event DataChangedDelegate? DataChanged;

        public EnergyMonitorService(LoggingService<EnergyMonitorService> logger,
                                    WeatherService weatherService,
                                    TimeZoneService timeZoneService,
                                    P1MeterContainer p1meterContainer,
                                    BatteryContainer batteryContainer,
                                    IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _weatherService = weatherService;
            _timeZoneService = timeZoneService;
            _serviceScopeFactory = serviceScopeFactory;
            _batteryContainer = batteryContainer;

            _scope = _serviceScopeFactory.CreateScope();

            _measurementService = _scope.ServiceProvider.GetRequiredService<QuarterlyMeasurementDataService>();
            _energyHistoryService = _scope.ServiceProvider.GetRequiredService<EnergyHistoryDataService>();
            _p1MeterService = _scope.ServiceProvider.GetRequiredService<P1MeterService>();
            _epexPricesService = _scope.ServiceProvider.GetRequiredService<EPEXPricesService>();

            _p1MeterContainer = p1meterContainer;
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

                var now = _timeZoneService.Now;
                var dateCeiling = now.DateCeilingQuarter();
                var delayTime = (dateCeiling - now).TotalSeconds + 5;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delayTime), cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, $"An error occurred during delay. Now {now}, Ceiling: {dateCeiling}.");
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
                var selectTime = now.DateFloorQuarter();

                // Skip if already stored for this quarter.
                bool exists = await _measurementService.Exists(async set =>
                {
                    var result = set.Any(m => m.Time == selectTime);
                    return await Task.FromResult(result);
                });

                if (exists)
                    continue;

                var p1Details = await _p1MeterContainer.GetDetails(p1Meter.Id!);
                var weatherData = await _weatherService.GetWeatherData();
                var prices = await _epexPricesService.GetPrices();

                var weatherHourData = weatherData.UurVerwachting
                    .FirstOrDefault(uv => uv.TimeStamp == selectTime.DateHour());

                var quarterlyInfo = prices
                    .FirstOrDefault(hi => hi.Time.DateFloorQuarter() == selectTime);

                await StoreMeasurement(p1Details!, quarterlyInfo, selectTime, weatherHourData, p1Meter.Id);

                DataChanged?.Invoke();

                _logger.LogInformation($"Energy data stored at {now} for {selectTime}");
            }
        }

        public async Task EnsureServicesAreInitialized(CancellationToken cancelationToken)
        {
            var tries = 0;

            while (!_weatherService.IsInitialized() && tries++ < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancelationToken);
            }

            tries = 0;

            while (!_epexPricesService.IsInitialized() && tries++ < 10)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancelationToken);
            }

            if (!(_epexPricesService.IsInitialized() && _weatherService.IsInitialized()))
                throw new InvalidOperationException("Day ahead service or weather service not initialized");
        }

        private async Task StoreMeasurement(
            P1Details p1Details,
            QuarterlyInfo? quarterlyInfo,
            DateTime time,
            UurVerwachting? hourExpectancy,
            string? meterId)
        {
            // Calculate grid import/export deltas from cumulative P1 meter tands.
            // Previous reading is cached; first reading of the session has no delta.
            double gridImportWh = 0.0;
            double gridExportWh = 0.0;

            if (_previousP1Details != null && _previousP1Time != null)
            {
                var gapMinutes = (time - _previousP1Time.Value).TotalMinutes;

                // Only use delta when readings are exactly one quarter apart (≤ 16 min).
                if (gapMinutes <= 16)
                {
                    gridImportWh = Math.Max(0,
                        (p1Details.PowerConsumedTariff1 - _previousP1Details.PowerConsumedTariff1) +
                        (p1Details.PowerConsumedTariff2 - _previousP1Details.PowerConsumedTariff2));

                    gridExportWh = Math.Max(0,
                        (p1Details.PowerProducedTariff1 - _previousP1Details.PowerProducedTariff1) +
                        (p1Details.PowerProducedTariff2 - _previousP1Details.PowerProducedTariff2));
                }
            }

            _previousP1Details = p1Details;
            _previousP1Time = time;

            // Read current SOC directly from the battery API so the record is
            // complete from the moment it is created, without waiting for
            // BatteriesService to update it.
            double socWh = 0.0;

            try
            {
                socWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal — BatteriesService will fill this in on its next cycle.
            }

            var measurement = new QuarterlyMeasurement
            {
                Time = time,
                GridImportWh = gridImportWh,
                GridExportWh = gridExportWh,
                BatteryStateOfChargeWh = socWh,
                // Battery mode is filled in by BatteriesService. Solar comes from InverterMeasurements.
            };

            await _measurementService.Add(new List<QuarterlyMeasurement> { measurement });

            // Store the cumulative P1 meter readings (meterstanden) so the raw meter history
            // is preserved, as in earlier versions. Values stay in Wh to match how
            // EnergyStatisticsService and FinancialResultsService read the tariff deltas.
            var history = new EnergyHistory
            {
                Time = time,
                MeterId = meterId,
                ConsumedTariff1 = p1Details.PowerConsumedTariff1,
                ConsumedTariff2 = p1Details.PowerConsumedTariff2,
                ProducedTariff1 = p1Details.PowerProducedTariff1,
                ProducedTariff2 = p1Details.PowerProducedTariff2,
                TarrifIndicator = p1Details.TariffIndicator,
                Temperature = hourExpectancy?.Temp ?? 0.0
            };

            await _energyHistoryService.Add(new List<EnergyHistory> { history });
        }
    }
}
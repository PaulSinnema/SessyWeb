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
            await WaitForOptionalServicesAsync(cancelationToken);

            // A missing meter section used to be completely silent: the loop below simply had
            // nothing to walk. Without meter readings there are no QuarterlyMeasurements either,
            // and the whole Energy statistics page stays empty (issue #4, same class).
            if (_p1MeterContainer.P1Meters.Count == 0)
            {
                if (!_noMetersLogged)
                {
                    _noMetersLogged = true;

                    _logger.LogWarning(
                        "EnergyMonitorService: no P1 meters configured (Sessy:Meters) — no meter readings and no " +
                        "quarterly measurements are stored, so the Energy statistics page stays empty.");
                }

                return;
            }

            _noMetersLogged = false;

            foreach (P1Meter? p1Meter in _p1MeterContainer.P1Meters)
            {
                var now = _timeZoneService.Now;
                var selectTime = now.DateFloorQuarter();

                // Skip if the meter readings for this quarter are already stored. Keyed on
                // EnergyHistory, not on QuarterlyMeasurement — BatteriesService may have created
                // the measurement row first, which would otherwise lose the meter readings.
                var meterId = p1Meter.Id;

                bool exists = await _energyHistoryService.Exists(async set =>
                {
                    var result = set.Any(h => h.Time == selectTime && h.MeterId == meterId);
                    return await Task.FromResult(result);
                });

                if (exists)
                    continue;

                var p1Details = await _p1MeterContainer.GetDetails(p1Meter.Id!);

                // Weather is decoration here — it contributes one nullable temperature. Without a
                // null guard a missing WeerOnline section threw on UurVerwachting and took the
                // meter reading down with it.
                var weatherData = await _weatherService.GetWeatherData();

                var weatherHourData = weatherData?.UurVerwachting?
                    .FirstOrDefault(uv => uv.TimeStamp == selectTime.DateHour());

                await StoreMeasurement(p1Details!, selectTime, weatherHourData, meterId);

                DataChanged?.Invoke();

                _logger.LogInformation($"Energy data stored at {now} for {selectTime}");
            }
        }

        private bool _firstCycle = true;
        private bool _noMetersLogged;
        private bool _weatherMissingLogged;

        /// <summary>
        /// Gives the weather service a moment to come up on the very first cycle, then carries on
        /// regardless.
        ///
        /// This used to throw when weather or prices were not initialized, which stopped the meter
        /// readings AND the QuarterlyMeasurement rows — while ConsumptionMonitorService, fixed in
        /// v1.0.96, kept filling the Consumption table. That is exactly how one installation ended
        /// up with a populated Consumption page and an empty Energy statistics page.
        ///
        /// Neither service is actually needed to store a meter reading: prices contributed nothing
        /// at all (the QuarterlyInfo was passed to StoreMeasurement and never read), and weather
        /// contributes one nullable temperature. WeatherService clears its own initialised flag at
        /// the top of every cycle, so a missing WeerOnline section means it is never true.
        /// </summary>
        private async Task WaitForOptionalServicesAsync(CancellationToken cancelationToken)
        {
            if (_firstCycle)
            {
                _firstCycle = false;

                var tries = 0;

                while (!_weatherService!.IsInitialized() && tries++ < 10)
                    await Task.Delay(TimeSpan.FromSeconds(5), cancelationToken);
            }

            bool weatherMissing = !_weatherService!.IsInitialized();

            // One line on the transition, not one per quarter.
            if (weatherMissing != _weatherMissingLogged)
            {
                _weatherMissingLogged = weatherMissing;

                _logger.LogWarning(weatherMissing
                    ? "EnergyMonitorService: no weather data — storing meter readings without a temperature."
                    : "EnergyMonitorService: weather data is back.");
            }
        }

        private async Task StoreMeasurement(
            P1Details p1Details,
            DateTime time,
            UurVerwachting? hourExpectancy,
            string? meterId)
        {
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

            // BatteriesService may already have created the record for this quarter; adding a
            // second one would violate the unique index on Time.
            bool measurementExists = await _measurementService.Exists(async set =>
            {
                var result = set.Any(m => m.Time == time);
                return await Task.FromResult(result);
            });

            if (!measurementExists)
            {
                var measurement = new QuarterlyMeasurement
                {
                    Time = time,
                    BatteryStateOfChargeWh = socWh,
                    // Battery mode is filled in by BatteriesService. Grid flows are derived from
                    // EnergyHistory meter readings, no longer stored on the measurement.
                };

                await _measurementService.Add(new List<QuarterlyMeasurement> { measurement });
            }

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
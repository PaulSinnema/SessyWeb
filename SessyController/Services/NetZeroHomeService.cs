using SessyCommon.Extensions;
using SessyController.Managers;
using SessyController.Services.Items;

namespace SessyController.Services
{
    public class NetZeroHomeService : BackgroundService, IDisposable
    {
        private LoggingService<DayAheadMarketService> _logger { get; set; }
        private P1MeterService _p1MeterService { get; set; }
        private BatteryContainer _batteryContainer { get; set; }
        private SolarInverterManager _solarInverterManager { get; set; }
        private TimeZoneService _timeZoneService { get; set; }

        private Sessions? _sessions { get; set; }

        private bool NetZeroHomeActive { get; set; } = false;

        private PowerInformationContainer? _powerInformation { get; set; }

        public NetZeroHomeService(LoggingService<DayAheadMarketService> logger,
                                  P1MeterService p1MeterService,
                                  BatteryContainer batteryContainer,
                                  SolarInverterManager solarInverterManager,
                                  TimeZoneService timeZoneService)
        {
            _logger = logger;
            _p1MeterService = p1MeterService;
            _batteryContainer = batteryContainer;
            _solarInverterManager = solarInverterManager;
            _timeZoneService = timeZoneService;
        }

        public void SetNetZeroHome(Sessions sessions, bool active)
        {
            if (active && sessions == null) throw new InvalidOperationException($"Sessions should not be null when passing active = true");

            _sessions = sessions;

            NetZeroHomeActive = true; //  active;
        }

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("Net zero home service started ...");

            // Loop to fetch prices every 5 seconds
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while processing.");
                }

                try
                {
                    int delayTime = 500; // Check again in 5 seconds

                    await Task.Delay(TimeSpan.FromMilliseconds(delayTime), cancelationToken);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay, keep processing {ex.ToDetailedString()}");
                }
            }

            _logger.LogWarning("Net zero home service stopped.");
        }

        private async Task Process(CancellationToken cancelationToken)
        {
            if (_sessions != null)
            {
                var now = _timeZoneService.Now;

                var hourlyInfo = _sessions.GetCurrentHourlyInfo();

                if (hourlyInfo != null)
                {
                    await NulOnMeter(hourlyInfo!);
                }
                else
                {
                    throw new InvalidOperationException($"Hourly info for current time not found, no processing for Null on metere");
                }
            }
        }

        private SemaphoreSlim NulOnMeterSemaphore = new SemaphoreSlim(1);
        
        /// <summary>
        /// Get the last read power information. This is updated every 2 seconds.
        /// </summary>
        public PowerInformationContainer? PowerInformation
        {
            get
            {
                NulOnMeterSemaphore.Wait();

                try
                {
                    return _powerInformation;
                }
                finally
                {
                    NulOnMeterSemaphore.Release();
                }
            }
        }

        /// <summary>
        /// Handle nul on meter.
        /// </summary>
        public async Task NulOnMeter(HourlyInfo currentHourlyInfo)
        {
            NulOnMeterSemaphore.Wait();

            try
            {
                _powerInformation = await GetPowerInformation();

                if (NetZeroHomeActive)
                {
                    await _batteryContainer.SetPowerSetpoint(_powerInformation.NetZeroHomeBatteryPower);
                }
            }
            finally
            {
                NulOnMeterSemaphore.Release();
            }
        }

        /// <summary>
        /// This class contains all fetched power data.
        /// </summary>
        public class PowerInformationContainer
        {
            public DateTime Time { get; set; }
            public double SolarPower { get; set; }
            public double TotalSetpoint { get; internal set; }
            public double BatteryPower { get; set; }
            public double NetPower { get; set; }

            public double HomeConsumption => SolarPower + BatteryPower + NetPower;
            public int NetZeroHomeBatteryPower => (int)-(SolarPower - HomeConsumption);


            public override string ToString()
            {
                return $"{Time}: SolarPower: {SolarPower}, BatterPower: {BatteryPower}, NetPower: {NetPower}, HomeConsumption: {HomeConsumption}";
            }
        }

        /// <summary>
        /// Get all power information in the system.
        /// </summary>
        private async Task<PowerInformationContainer> GetPowerInformation()
        {
            var powerInformation = new PowerInformationContainer
            {
                Time = _timeZoneService.Now,
                TotalSetpoint = await GetPowerSetPointInWatts(),
                SolarPower = await GetSolarPower(),
                BatteryPower = await GetBatteryPower(),
                NetPower = await GetNetPower()
            };

            return powerInformation;
        }

        private async Task<double> GetPowerSetPointInWatts()
        {
            var powerSetpoint = await _batteryContainer.GetPowerSetpoint();

            return powerSetpoint;
        }

        /// <summary>
        /// Get the net power.
        /// </summary>
        private async Task<double> GetNetPower()
        {
            var totalNetPower = await _p1MeterService.GetTotalACPowerInWatts();

            return totalNetPower;
        }

        /// <summary>
        /// Get the battery power of all batteries.
        /// </summary>
        /// <returns></returns>
        private async Task<double> GetBatteryPower()
        {
            var totalACPowerInWatts = await _batteryContainer.GetTotalACPowerInWatts();

            return totalACPowerInWatts;
        }

        /// <summary>
        /// Get the solar power of all inverters.
        /// </summary>
        private async Task<double> GetSolarPower()
        {
            var totalSolarPower = await _solarInverterManager.GetTotalACPowerInWatts();

            return totalSolarPower;
        }

        private bool _isDisposed = false;

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
            }

            base.Dispose();
        }
    }
}

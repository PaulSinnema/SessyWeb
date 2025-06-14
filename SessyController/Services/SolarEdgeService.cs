using Djohnnie.SolarEdge.ModBus.TCP;
using Djohnnie.SolarEdge.ModBus.TCP.Constants;
using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Providers;
using SessyData.Model;
using SessyData.Services;
using Types = Djohnnie.SolarEdge.ModBus.TCP.Types;

namespace SessyController.Services
{
    /// <summary>
    /// This class is used to read the result from the SolarEdge inverter.
    /// </summary>
    public class SolarEdgeService : BackgroundService
    {
        private const ushort acPowerStartAddress = 0x9C93;
        private const ushort acPowerSFStartAddress = 0x9C94;
        private const ushort statusStartAddress = 0x9CAB;

        private const ushort enableDynamicPowerControl = 0xF300;
        private const ushort advancedPwrControlEn = 0xF142;
        private const ushort reactivePwrConfig = 0xF104;

        private const ushort RestorePowerControlDefaultSettings = 0xF101;

        private const ushort dynamicActivePowerLimit = 0xF322;
        private const ushort dynamicReactivePowerLimit = 0xF324;

        private const ushort activeReactivePreference = 0xF308;
        private const ushort cosPhiQPreference = 0xF309;
        private const ushort activePowerLimit = 0xF30C;
        private const ushort reactivePowerLimit = 0xF30E;

        private readonly IConfiguration _configuration;
        private LoggingService<SolarEdgeService> _logger { get; set; }
        private IHttpClientFactory _httpClientFactory { get; set; }
        private PowerSystemsConfig _powerSystemsConfig { get; set; }
        private IServiceScope _scope { get; set; }
        private TcpClientProvider _tcpClientProvider { get; set; }
        private ConfigurationService _configurationService { get; set; }

        public double ActualSolarPowerInWatts { get; private set; }

        private TimeZoneService _timeZoneService;
        private SolarEdgeDataService _solarEdgeDataService;

        public SolarEdgeService(IConfiguration configuration,
                                      LoggingService<SolarEdgeService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptions<PowerSystemsConfig> powerSystemsConfig,
                                      IServiceScopeFactory serviceScopeFactory)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _powerSystemsConfig = powerSystemsConfig.Value;

            _scope = serviceScopeFactory.CreateScope();

            _tcpClientProvider = _scope.ServiceProvider.GetRequiredService<TcpClientProvider>();
            _configurationService = _scope.ServiceProvider.GetRequiredService<ConfigurationService>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _solarEdgeDataService = _scope.ServiceProvider.GetRequiredService<SolarEdgeDataService>();
        }

        protected override async Task ExecuteAsync(CancellationToken cancelationToken)
        {
            _logger.LogWarning("SolarEdge service started ...");

            // Loop to fetch prices every second
            while (!cancelationToken.IsCancellationRequested)
            {
                try
                {
                    await Process(cancelationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while processing SolarEdge data.");
                }

                try
                {
                    int delayTime = 1; // Check again every seconds

                    await Task.Delay(TimeSpan.FromSeconds(delayTime), cancelationToken);
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

            _logger.LogWarning("SolarEdge stopped.");
        }

        private Dictionary<string, Dictionary<DateTime, double>> CollectedPowerData { get; set; } = new();
        private DateTime? lastDataStoredTime { get; set; } = null;

        private async Task Process(CancellationToken cancelationToken)
        {
            foreach (var powerSystemConfig in _powerSystemsConfig.Endpoints)
            {
                foreach (var config in powerSystemConfig.Value)
                {
                    var level = _timeZoneService.GetSunlightLevel(config.Value.Latitude, config.Value.Longitude);

                    if (level == SolCalc.Data.SunlightLevel.Daylight)
                    {
                        // The sun is visible (over the horizon).
                        ActualSolarPowerInWatts = await GetACPowerInWatts(config.Key);
                        var date = _timeZoneService.Now;

                        if (!CollectedPowerData.ContainsKey(config.Key))
                        {
                            CollectedPowerData.Add(config.Key, new Dictionary<DateTime, double>());
                        }

                        CollectedPowerData[config.Key].Add(date, ActualSolarPowerInWatts);

                        StoreData(CollectedPowerData);
                    }
                }
            }
        }

        /// <summary>
        /// Get the total power output for all configured solar arrays.
        /// </summary>
        public async Task<double> GetTotalACPowerInWatts()
        {
            var power = 0.0;

            foreach (var powerSystemConfig in _powerSystemsConfig.Endpoints)
            {
                foreach (var config in powerSystemConfig.Value)
                {
                    power += await GetACPowerInWatts(config.Key);
                }
            }

            return power;
        }

        /// <summary>
        /// Store the collected data in the DB.
        /// </summary>
        private void StoreData(Dictionary<string, Dictionary<DateTime, double>> collectedPowerData)
        {
            foreach (var collectionKeyValue in collectedPowerData)
            {
                var id = collectionKeyValue.Key;
                var collection = collectionKeyValue.Value;

                var hours = collection
                    .Select(c => c.Key.DateFloorQuarter())
                    .Distinct()
                    .OrderBy(date => date);

                if (hours.Count() > 1) // A new hour has started.
                {
                    var date = hours.First();
                    var count = collection.Where(c => c.Key.DateFloorQuarter() == date).Count();
                    var total = collection.Where(c => c.Key.DateFloorQuarter() == date).Sum(c => c.Value);
                    var power = total / count;

                    List<SolarEdgeData> list = new List<SolarEdgeData>
                    {
                        new SolarEdgeData
                        {
                            InverterId = id,
                            Time = date,
                            Power = power
                        }
                    };

                    _solarEdgeDataService.Add(list);

                    foreach (var item in collection.ToList())
                    {
                        if (item.Key.DateFloorQuarter() == date)
                            collection.Remove(item.Key);
                    }
                }
            }
        }

        /// <summary>
        /// Get a SolarEdge Modbus client for id.
        /// </summary>
        private async Task<ModbusClient> GetModbusClient(string id)
        {
            var tries = 0;
            Exception? exception = null;

            do
            {
                try
                {
                    return await _tcpClientProvider.GetModbusClient("SolarEdge", id);
                }
                catch (Exception ex)
                {
                    exception = ex;
                    tries++;
                    _logger.LogInformation($"Failed to get a Modbus client for 'SolarEdge' with id: {id}. Retrying {tries}");
                }
            } while (tries < 10);

            throw new InvalidOperationException($"Could not get Modbus client for 'SolarEdge' with id: {id}", exception);
        }

        /// <summary>
        /// Get the AC power output from the inverter.
        /// </summary>
        /// <returns>Unscaled AC Power output</returns>
        public async Task<ushort> GetACPower(string id)
        {
            using var client = await GetModbusClient(id);

            var result = await client.ReadHoldingRegisters<Types.UInt16>(SunspecConsts.I_AC_Power);

            return result.Value;
        }

        /// <summary>
        /// Gets the scaling factor to be used to convert AC power to watts
        /// </summary>
        /// <returns>Scaling factor</returns>
        public async Task<short> GetACPowerScaleFactor(string id)
        {
            using var client = await GetModbusClient(id);

            var result = await client.ReadHoldingRegisters<Types.Int16>(SunspecConsts.I_AC_Power_SF);

            return result.Value;
        }

        /// <summary>
        /// Get the inverters power output in Watts.
        /// </summary>
        public async Task<double> GetACPowerInWatts(string id)
        {
            var powerOutput = await GetACPower(id);
            var scaleFactor = await GetACPowerScaleFactor(id);

            return powerOutput * Math.Pow(10, scaleFactor);
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// </summary>
        public async Task<ushort> GetStatus(string id)
        {
            using var client = await GetModbusClient(id);

            var result = await client.ReadHoldingRegisters<Types.UInt16>(SunspecConsts.I_Status);

            return result.Value;
        }

        public async Task EnableDynamicPower(string id)
        {
            try
            {
                using var client = await GetModbusClient(id);

                await client.WriteSingleRegister(0xF142, (uint)1);
                await client.WriteSingleRegister(0xF104, (uint)4);

                await CommitValues(client);

                var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Types.Int32>(0xF142);
                var reactivePwrConfigRead = await client.ReadHoldingRegisters<Types.Int32>(0xF104);

                if (enableDynamicPowerControlRead.Value != 1 ||
                    reactivePwrConfigRead.Value != 4)
                    throw new InvalidOperationException($"Enabling advanced power control failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Enable advanced power control failed {ex.ToDetailedString()}");
            }
        }

        private async Task<bool> CommitValues(ModbusClient client)
        {
            ushort result = 0;

            await client.WriteSingleRegister(0xF100, 1);

            for (int i = 0; i < 30; i++)
            {
                var read = await client.ReadHoldingRegisters<Types.UInt16>(0xF100);

                if (read.Value == 0x00)
                {
                    _logger.LogInformation("Modbus commit succeeded");
                    return true;
                }

                result = read.Value;

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            throw new InvalidOperationException($"Failed to commit values, last error code: {result}");
        }

        public async Task RestoreDynamicPowerSettings(string id)
        {
            UInt32 advancedPwrControlEnValue = 1;
            UInt32 reactivePwrConfigValue = 0;
            UInt16 restorePowerControlDefaultSettingsValue = 1;

            try
            {
                using var client = await GetModbusClient(id);

                await client.WriteSingleRegister(0xF101, restorePowerControlDefaultSettingsValue);

                await CommitValues(client);

                var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Types.Int32>(0xF142);
                var reactivePwrConfigRead = await client.ReadHoldingRegisters<Types.Int32>(0xF104);

                if (enableDynamicPowerControlRead.Value != advancedPwrControlEnValue ||
                    reactivePwrConfigRead.Value != reactivePwrConfigValue)
                    throw new InvalidOperationException($"Restore advanced power settings failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Restore advanced power control settings failed {ex.ToDetailedString()}");
            }
        }

        //public async Task<ushort> GetCosPhiQPreference(string id)
        //{
        //    using var client = await GetModbusClient(id);

        //    var preference = await client.ReadHoldingRegisters<Types.UInt16>(cosPhiQPreference);

        //    return preference.Value;
        //}

        //public async Task<ushort> GetActiveReactivePreference(string id)
        //{
        //    using var client = await GetModbusClient(id);

        //    var preference = await client.ReadHoldingRegisters<Types.UInt16>(activeReactivePreference);

        //    return preference.Value;
        //}

        public async Task<float> GetActivePowerLimit(string id)
        {
            using var client = await GetModbusClient(id);

            var powerSet = await client.ReadHoldingRegisters<Types.UInt16>(0xF001);

            return powerSet.Value;
        }

        //public async Task<float> GetReactivePowerLimit(string id)
        //{
        //    using var client = await GetModbusClient(id);

        //    var powerSet = await client.ReadHoldingRegisters<Types.Int16>(reactivePowerLimit);

        //    return powerSet.Value;
        //}

        //public async Task<float> GetDynamicActivePowerLimit(string id)
        //{
        //    using var client = await GetModbusClient(id);

        //    var powerSet = await client.ReadHoldingRegisters<Types.Float32>(dynamicActivePowerLimit);

        //    return powerSet.Value;
        //}

        public async Task SetActivePowerLimit(string id, UInt16 power)
        {
            if (power < 0 || power > 100) throw new ArgumentOutOfRangeException(nameof(power));

            using var client = await GetModbusClient(id);

            await client.WriteSingleRegister(0xF001, power);
        }

        //public async Task SetReactivePowerLimit(string id, float power)
        //{
        //    if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

        //    using var client = await GetModbusClient(id);

        //    await client.WriteMultipleRegisters(reactivePowerLimit, power);
        //}

        //public async Task SetDynamicActivePowerLimit(string id, float power)
        //{
        //    if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

        //    using var client = await GetModbusClient(id);

        //    await client.WriteMultipleRegisters(dynamicActivePowerLimit, power);
        //}

        //public async Task SetDynamicReactivePowerLimit(string id, float power)
        //{
        //    if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

        //    using var client = await GetModbusClient(id);

        //    await client.WriteMultipleRegisters(dynamicReactivePowerLimit, power);
        //}
    }
}


using Djohnnie.SolarEdge.ModBus.TCP;
using Djohnnie.SolarEdge.ModBus.TCP.Constants;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Providers;
using SessyData.Model;
using SessyData.Services;
using Endpoint = SessyCommon.Configurations.Endpoint;

namespace SessyController.Services.InverterServices
{
    public class SunspecInverterService : ISolarInverterService
    {
        public string ProviderName { get; private set; }

        private LoggingService<SolarEdgeInverterService> _logger { get; set; }
        private IOptionsMonitor<PowerSystemsConfig> _powerSystemsConfigMonitor { get; set; }
        private PowerSystemsConfig _powerSystemConfig { get; set; }
        private IServiceScope _scope { get; set; }
        private TcpClientProvider _tcpClientProvider { get; set; }
        public double ActualSolarPowerInWatts { get; private set; }
        public Dictionary<string, Endpoint> Endpoints => _powerSystemConfig!.Endpoints[ProviderName];
        public double TotalCapacity => Endpoints.Sum(ep => ep.Value.InverterMaxCapacity);
        private bool _IsRunning { get; set; } = false;

        private TimeZoneService _timeZoneService;
        private SolarEdgeDataService _solarEdgeDataService;

        public SunspecInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      string providerName,
                                      IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor,
                                      IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            ProviderName = providerName;
            _powerSystemsConfigMonitor = powerSystemsConfigMonitor;
            _powerSystemConfig = _powerSystemsConfigMonitor.CurrentValue;

            _powerSystemsConfigMonitor.OnChange((config) => _powerSystemConfig = config);

            _scope = serviceScopeFactory.CreateScope();

            _tcpClientProvider = _scope.ServiceProvider.GetRequiredService<TcpClientProvider>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _solarEdgeDataService = _scope.ServiceProvider.GetRequiredService<SolarEdgeDataService>();
        }

        public async Task Start(CancellationToken cancelationToken)
        {
            _logger.LogWarning("SolarEdge service started ...");

            _IsRunning = true;

            await CleanUpWrongData();

            // Loop to fetch prices every second
            while (!cancelationToken.IsCancellationRequested && _IsRunning)
            {
                try
                {
                    await Process(cancelationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "An error occurred while processing SolarEdge data.");
                }

                try
                {
                    int delayTime = 1; // Check again every seconds

                    await Task.Delay(TimeSpan.FromSeconds(delayTime), cancelationToken).ConfigureAwait(false);
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

        public async Task Stop(CancellationToken cancelationToken)
        {
            _IsRunning = false;

            await Task.Yield();
        }

        private async Task CleanUpWrongData()
        {
            await _solarEdgeDataService.RemoveWrongData(ProviderName);
        }

        private Dictionary<string, Dictionary<DateTime, double>> CollectedPowerData { get; set; } = new();

        private async Task Process(CancellationToken cancelationToken)
        {
            foreach (var powerSystemConfig in _powerSystemConfig.Endpoints)
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

                        await StoreData(CollectedPowerData);
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

            foreach (var endpoint in Endpoints)
            {
                power += await GetACPowerInWatts(endpoint.Key).ConfigureAwait(false);
            }

            return power;
        }

        public async Task ThrottleInverterToPercentage(ushort percentage)
        {
            if (TotalCapacity <= 0.0) throw new InvalidOperationException($"InverterMaxCapacity not set or wrong in config for endpoint {ProviderName}");

            foreach (var endpoint in Endpoints)
            {
                var id = endpoint.Key;
                var isEnabled = await IsDynamicPowerEnabled(id).ConfigureAwait(false);

#if DEBUG
                _logger.LogWarning($"IsDynamicPowerEnabled({id}) => {isEnabled}");
                _logger.LogWarning($"DEBUG: {ProviderName}: SetActivePowerLimit({endpoint.Key}, {percentage})");
                await Task.Delay(1).ConfigureAwait(false); // To prevent a warning for the keyword 'async'.
#else
                _logger.LogInformation($"DEBUG: {ProviderName}: SetActivePowerLimit({endpoint.Key}, {percentage})");

                if (!isEnabled)
                {
                    await EnableDynamicPower(id).ConfigureAwait(false); // One time initialization.
                }

                await SetActivePowerLimit(id, percentage).ConfigureAwait(false);
#endif
            }
        }

        /// <summary>
        /// Store the collected data in the DB.
        /// </summary>
        private async Task StoreData(Dictionary<string, Dictionary<DateTime, double>> collectedPowerData)
        {
            var now = _timeZoneService.Now;

            foreach (var collectionKeyValue in collectedPowerData)
            {
                var id = collectionKeyValue.Key;
                var collection = collectionKeyValue.Value;

                // Group all samples by their quarter-hour timestamp
                var quarterGroups = collection
                    .GroupBy(c => c.Key.DateFloorQuarter())
                    .OrderBy(g => g.Key)
                    .ToList();

                foreach (var quarterGroup in quarterGroups)
                {
                    var quarter = quarterGroup.Key;

                    var nextQuarter = quarter.AddMinutes(15);

                    // Only store completed quarters
                    if (now < nextQuarter)
                        continue;

                    var values = quarterGroup
                            .Select(g => g.Value)
                            .Where(v => !double.IsNaN(v) && !double.IsInfinity(v))
                            .ToList();

                    if (values.Count < 1)
                        continue;

                    var averagePower = values.Average();

                    var entry = new SolarInverterData
                    {
                        ProviderName = ProviderName,
                        InverterId = id,
                        Time = quarter,
                        Power = averagePower
                    };

                    await _solarEdgeDataService.Add(new List<SolarInverterData> { entry });

                    // Remove processed items from memory
                    foreach (var item in quarterGroup)
                    {
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
                    return await _tcpClientProvider.GetModbusClient(ProviderName, id).ConfigureAwait(false);
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
            var tries = 0;

            while (tries < 10)
            {
                try
                {
                    using var client = await GetModbusClient(id).ConfigureAwait(false);
                    {
                        var result = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.I_AC_Power).ConfigureAwait(false);

                        return result.Value;
                    }
                }
                catch (Exception)
                {
                }

                await Task.Delay(1000);
                tries++;
            }

            throw new InvalidOperationException($"Failed to get modbus data for AC Power after 10 retries");
        }

        /// <summary>
        /// Gets the scaling factor to be used to convert AC power to watts
        /// </summary>
        /// <returns>Scaling factor</returns>
        public async Task<short> GetACPowerScaleFactor(string id)
        {
            using var client = await GetModbusClient(id).ConfigureAwait(false);

            var result = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int16>(SunspecConsts.I_AC_Power_SF).ConfigureAwait(false);

            return result.Value;
        }

        /// <summary>
        /// Get the inverters power output in Watts.
        /// </summary>
        public async Task<double> GetACPowerInWatts(string id)
        {
            var powerOutput = await GetACPower(id).ConfigureAwait(false);
            var scaleFactor = await GetACPowerScaleFactor(id).ConfigureAwait(false);

            return powerOutput * Math.Pow(10, scaleFactor);
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// </summary>
        public async Task<ushort> GetStatus(string id)
        {
            using var client = await GetModbusClient(id).ConfigureAwait(false);

            var result = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.I_Status).ConfigureAwait(false);

            return result.Value;
        }

        public async Task<bool> IsDynamicPowerEnabled(string id)
        {
            uint reactivePwrConfigValue = 4;

            using var client = await GetModbusClient(id).ConfigureAwait(false);
            
            var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.ReactivePwrConfig).ConfigureAwait(false);

            return enableDynamicPowerControlRead.Value == reactivePwrConfigValue;
        }

        /// <summary>
        /// Enable dynamic power mode. Call this routine before you try to set the power limit.
        /// It will take several minutes for the Inverter to restart after calling this routine.
        /// </summary>
        public async Task EnableDynamicPower(string id)
        {
            uint advancedPwrControlEnValue = 1;
            uint reactivePwrConfigValue = 4;

            try
            {
                using var client = await GetModbusClient(id).ConfigureAwait(false);

                await client.WriteSingleRegister(SunspecConsts.AdvancedPwrControlEn, advancedPwrControlEnValue).ConfigureAwait(false);
                await client.WriteSingleRegister(SunspecConsts.ReactivePwrConfig, reactivePwrConfigValue).ConfigureAwait(false);

                await CommitValues(client).ConfigureAwait(false);

                var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.AdvancedPwrControlEn).ConfigureAwait(false);
                var reactivePwrConfigRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.ReactivePwrConfig).ConfigureAwait(false);

                if (enableDynamicPowerControlRead.Value != advancedPwrControlEnValue ||
                    reactivePwrConfigRead.Value != reactivePwrConfigValue)
                    throw new InvalidOperationException($"Enabling advanced power control failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Enable advanced power control failed {ex.ToDetailedString()}");
            }
        }

        /// <summary>
        /// Commit values into the registers of the Inverter logic.
        /// </summary>
        private async Task<bool> CommitValues(ModbusClient client)
        {
            ushort result = 0;

            await client.WriteSingleRegister(SunspecConsts.CommitPowerControlSettings, 1).ConfigureAwait(false);

            for (int i = 0; i < 30; i++)
            {
                var read = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.CommitPowerControlSettings).ConfigureAwait(false);

                if (read.Value == 0x00)
                {
                    _logger.LogInformation("Modbus commit succeeded");
                    return true;
                }

                result = read.Value;

                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Failed to commit values, last error code: {result}");
        }

        /// <summary>
        /// Disables the dynamic power mode. After this routine is called you can no longer 
        /// set the power limit.
        /// It will take several minutes for the Inverter to restart after calling this routine.
        /// </summary>
        public async Task RestoreDynamicPowerSettings(string id)
        {
            uint advancedPwrControlEnValue = 1;
            uint reactivePwrConfigValue = 0;
            ushort restorePowerControlDefaultSettingsValue = 1;

            try
            {
                using var client = await GetModbusClient(id).ConfigureAwait(false);

                await client.WriteSingleRegister(SunspecConsts.RestorePowerControlDefaultSettings, restorePowerControlDefaultSettingsValue).ConfigureAwait(false);

                await CommitValues(client).ConfigureAwait(false);

                var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.AdvancedPwrControlEn).ConfigureAwait(false);
                var reactivePwrConfigRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.ReactivePwrConfig).ConfigureAwait(false);

                if (enableDynamicPowerControlRead.Value != advancedPwrControlEnValue ||
                    reactivePwrConfigRead.Value != reactivePwrConfigValue)
                    throw new InvalidOperationException($"Restore advanced power settings failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Restore advanced power control settings failed {ex.ToDetailedString()}");
            }
        }

        /// <summary>
        /// Gets the current active power limit.
        /// </summary>
        public async Task<float> GetActivePowerLimit(string id)
        {
            using var client = await GetModbusClient(id).ConfigureAwait(false);

            var powerSet = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.ActivePowerLimit).ConfigureAwait(false);

            return powerSet.Value;
        }

        /// <summary>
        /// Sets the current active power limit.
        /// You must enable the dynamic power mode before you can change the limit with
        /// this method.
        /// </summary>
        public async Task SetActivePowerLimit(string id, ushort power)
        {
            if (power < 0 || power > 100) throw new ArgumentOutOfRangeException(nameof(power));

            using var client = await GetModbusClient(id).ConfigureAwait(false);

            await client.WriteSingleRegister(0xF001, power).ConfigureAwait(false);
        }
    }
}
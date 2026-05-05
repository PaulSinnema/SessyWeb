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

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private SettingsConfig _settingsConfig;

        private LoggingService<SolarEdgeInverterService> _logger { get; set; }
        private IOptionsMonitor<PowerSystemsConfig> _powerSystemsConfigMonitor { get; set; }
        private PowerSystemsConfig _powerSystemConfig { get; set; }
        private IServiceScope _scope { get; set; }
        private TcpClientProvider _tcpClientProvider { get; set; }

        /// <summary>
        /// Most recently measured AC power in Watts. Updated every second by the
        /// background Process() loop. Use this for non-blocking reads instead of
        /// calling GetACPowerInWatts() directly.
        /// </summary>
        public double ActualSolarPowerInWatts { get; private set; }

        public Dictionary<string, Endpoint> Endpoints => _powerSystemConfig!.Endpoints[ProviderName];
        public double TotalCapacity => Endpoints.Sum(ep => ep.Value.InverterMaxCapacity);

        private bool _IsRunning { get; set; } = false;

        private TimeZoneService _timeZoneService;
        private SolarInverterDataService _solarEdgeDataService;

        // Serializes all Modbus operations to prevent transaction ID mismatches
        // when multiple callers (curtailment loop, power read loop) access the
        // inverter concurrently over the same TCP connection.
        private readonly SemaphoreSlim _modbusSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// True when the inverter is reachable via Modbus TCP.
        /// Set by SolarInverterManager health check — not by this class itself.
        /// </summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>
        /// Base implementation does not support a cloud fallback.
        /// Override in provider-specific subclasses (e.g. SolarEdgeInverterService).
        /// </summary>
        public virtual bool SupportsFallback => false;

        /// <summary>
        /// Base implementation returns 0.0 — no fallback available.
        /// Override in provider-specific subclasses.
        /// </summary>
        public virtual Task<double> GetFallbackACPowerInWattsAsync()
            => Task.FromResult(0.0);

        public SunspecInverterService(LoggingService<SolarEdgeInverterService> logger,
                                      string providerName,
                                      IHttpClientFactory httpClientFactory,
                                      IOptionsMonitor<SettingsConfig> settingsConfig,
                                      IOptionsMonitor<PowerSystemsConfig> powerSystemsConfigMonitor,
                                      IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            ProviderName = providerName;
            _settingsConfigMonitor = settingsConfig;
            _settingsConfig = _settingsConfigMonitor.CurrentValue;
            _powerSystemsConfigMonitor = powerSystemsConfigMonitor;
            _powerSystemConfig = _powerSystemsConfigMonitor.CurrentValue;

            _settingsConfigMonitor.OnChange((config) => _settingsConfig = config);
            _powerSystemsConfigMonitor.OnChange((config) => _powerSystemConfig = config);

            _scope = serviceScopeFactory.CreateScope();

            _tcpClientProvider = _scope.ServiceProvider.GetRequiredService<TcpClientProvider>();
            _timeZoneService = _scope.ServiceProvider.GetRequiredService<TimeZoneService>();
            _solarEdgeDataService = _scope.ServiceProvider.GetRequiredService<SolarInverterDataService>();
        }

        public async Task Start(CancellationToken cancelationToken)
        {
            _logger.LogWarning("SolarEdge service started ...");

            _IsRunning = true;

            await CleanUpWrongData().ConfigureAwait(false);

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
                    await Task.Delay(TimeSpan.FromSeconds(1), cancelationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation exception during delay.
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Something went wrong during delay: {ex.ToDetailedString()}");
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
            await _solarEdgeDataService.RemoveWrongData(ProviderName).ConfigureAwait(false);
        }

        private Dictionary<string, Dictionary<DateTime, double>> CollectedPowerData { get; set; } = new();

        private async Task Process(CancellationToken cancelationToken)
        {
            // Skip processing when the inverter is offline.
            if (!IsAvailable)
            {
                ActualSolarPowerInWatts = 0.0;
                return;
            }

            foreach (var powerSystemConfig in _powerSystemConfig.Endpoints)
            {
                var level = _timeZoneService.GetSunlightLevel(_settingsConfig.Latitude, _settingsConfig.Longitude);

                foreach (var config in powerSystemConfig.Value)
                {
                    if (level == SolCalc.Data.SunlightLevel.Daylight)
                    {
                        ActualSolarPowerInWatts = await GetACPowerInWatts(config.Key).ConfigureAwait(false);
                        var date = _timeZoneService.Now;

                        if (!CollectedPowerData.ContainsKey(config.Key))
                            CollectedPowerData.Add(config.Key, new Dictionary<DateTime, double>());

                        CollectedPowerData[config.Key].Add(date, ActualSolarPowerInWatts);

                        await StoreData(CollectedPowerData).ConfigureAwait(false);
                    }
                    else
                    {
                        // Outside daylight hours — reset to zero.
                        ActualSolarPowerInWatts = 0.0;
                    }
                }
            }
        }

        // In SunspecInverterService:
        public async Task CheckAvailabilityAsync()
        {
            // Deliberately bypasses IsAvailable — used by health check to detect recovery.
            using var client = await GetModbusClient(Endpoints.First().Key).ConfigureAwait(false);
            await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int16>(
                SunspecConsts.I_AC_Power).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the total power output in Watts for all configured endpoints.
        /// Used by SolarInverterManager health check.
        /// Returns 0.0 when the inverter is offline.
        /// </summary>
        public async Task<double> GetTotalACPowerInWatts()
        {
            if (!IsAvailable)
                return 0.0;

            var power = 0.0;

            foreach (var endpoint in Endpoints)
                power += await GetACPowerInWatts(endpoint.Key).ConfigureAwait(false);

            return power;
        }

        /// <summary>
        /// Get the total AC lifetime energy production in Wh for all endpoints.
        /// Returns 0.0 when the inverter is offline.
        /// </summary>
        public async Task<double> GetTotalACEnergyInWh()
        {
            if (!IsAvailable)
                return 0.0;

            var energy = 0.0;

            foreach (var endpoint in Endpoints)
                energy += await GetACEnergyInWh(endpoint.Key).ConfigureAwait(false);

            return energy;
        }

        /// <summary>
        /// Get the AC power output in Watts for a single endpoint.
        /// Reads I_AC_Power and I_AC_Power_SF in a single Modbus transaction
        /// to prevent race conditions between the value and scale factor reads.
        /// Returns 0.0 when the inverter is offline.
        /// </summary>
        public async Task<double> GetACPowerInWatts(string id)
        {
            if (!IsAvailable)
                return 0.0;

            return await ExecuteModbusAsync(async () =>
            {
                try
                {
                    using var client = await GetModbusClient(id).ConfigureAwait(false);

                    // Read power value and scale factor atomically in one Modbus transaction.
                    var (power, scaleFactor) = await client.ReadHoldingRegistersBlock<
                        Djohnnie.SolarEdge.ModBus.TCP.Types.Int16,
                        Djohnnie.SolarEdge.ModBus.TCP.Types.Int16>(
                            SunspecConsts.I_AC_Power,
                            SunspecConsts.I_AC_Power_SF).ConfigureAwait(false);

                    return power.Value * Math.Pow(10, scaleFactor.Value);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to get AC power for endpoint {id}", ex);
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Get the AC lifetime energy production in Wh for a single endpoint.
        /// Reads I_AC_Energy_WH and I_AC_Energy_WH_SF in a single Modbus transaction.
        /// Returns 0.0 when the inverter is offline.
        /// </summary>
        public async Task<double> GetACEnergyInWh(string id)
        {
            if (!IsAvailable)
                return 0.0;

            return await ExecuteModbusAsync(async () =>
            {
                try
                {
                    using var client = await GetModbusClient(id).ConfigureAwait(false);

                    // Read energy value and scale factor atomically in one Modbus transaction.
                    var (energy, scaleFactor) = await client.ReadHoldingRegistersBlock<
                        Djohnnie.SolarEdge.ModBus.TCP.Types.Acc32,
                        Djohnnie.SolarEdge.ModBus.TCP.Types.Int16>(
                            SunspecConsts.I_AC_Energy_WH,
                            SunspecConsts.I_AC_Energy_WH_SF).ConfigureAwait(false);

                    return energy.Value * Math.Pow(10, scaleFactor.Value);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to get AC energy for endpoint {id}", ex);
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// Returns 0 when the inverter is offline.
        /// </summary>
        public async Task<ushort> GetStatus(string id)
        {
            if (!IsAvailable)
                return 0;

            return await ExecuteModbusAsync(async () =>
            {
                using var client = await GetModbusClient(id).ConfigureAwait(false);
                var result = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.I_Status).ConfigureAwait(false);
                return result.Value;
            }).ConfigureAwait(false);
        }

        public async Task ThrottleInverterToPercentage(ushort percentage)
        {
            if (!IsAvailable)
                return;

            if (TotalCapacity <= 0.0)
                throw new InvalidOperationException($"InverterMaxCapacity not set or wrong in config for endpoint {ProviderName}");

            foreach (var endpoint in Endpoints)
            {
                var id = endpoint.Key;
                var isEnabled = await IsDynamicPowerEnabled(id).ConfigureAwait(false);

#if DEBUG
                _logger.LogWarning($"IsDynamicPowerEnabled({id}) => {isEnabled}");
                _logger.LogWarning($"DEBUG: {ProviderName}: SetActivePowerLimit({endpoint.Key}, {percentage})");
                await Task.Delay(1).ConfigureAwait(false);
#else
                _logger.LogInformation($"SetActivePowerLimit({endpoint.Key}, {percentage})");

                if (!isEnabled)
                    await EnableDynamicPower(id).ConfigureAwait(false);

                await SetActivePowerLimit(id, percentage).ConfigureAwait(false);
#endif
            }
        }

        /// <summary>
        /// Get a SolarEdge Modbus client for the given endpoint id.
        /// Must be called inside ExecuteModbusAsync to ensure serialized access.
        /// </summary>
        private async Task<ModbusClient> GetModbusClient(string id)
        {
            try
            {
                return await _tcpClientProvider.GetModbusClient(ProviderName, id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not get Modbus client for '{ProviderName}' with id: {id}", ex);
            }
        }

        /// <summary>
        /// Executes a Modbus operation exclusively via a semaphore, preventing
        /// concurrent TCP access that causes transaction ID mismatches.
        /// </summary>
        private async Task<T> ExecuteModbusAsync<T>(Func<Task<T>> operation)
        {
            var tries = 0;
            Exception? exception = null;

            while (tries++ < 10)
            {
                await _modbusSemaphore.WaitAsync().ConfigureAwait(false);

                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch(Exception ex)
                {
                    exception = ex;
                    await Task.Delay(1000);
                }
                finally
                {
                    _modbusSemaphore.Release();
                }
            }

            throw new InvalidOperationException($"Execute Modbus async failed", exception);
        }

        private async Task ExecuteModbusAsync(Func<Task> operation)
        {
            await _modbusSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await operation().ConfigureAwait(false);
            }
            finally
            {
                _modbusSemaphore.Release();
            }
        }

        public async Task<bool> IsDynamicPowerEnabled(string id)
        {
            return await ExecuteModbusAsync(async () =>
            {
                uint reactivePwrConfigValue = 4;

                using var client = await GetModbusClient(id).ConfigureAwait(false);
                var result = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.ReactivePwrConfig).ConfigureAwait(false);

                return result.Value == reactivePwrConfigValue;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Enables dynamic power control mode on the inverter.
        /// Must be called once before SetActivePowerLimit can be used.
        /// Note: the inverter restarts after this call (takes several minutes).
        /// </summary>
        public async Task EnableDynamicPower(string id)
        {
            await ExecuteModbusAsync(async () =>
            {
                uint advancedPwrControlEnValue = 1;
                uint reactivePwrConfigValue = 4;

                try
                {
                    using var client = await GetModbusClient(id).ConfigureAwait(false);

                    await client.WriteSingleRegister(SunspecConsts.AdvancedPwrControlEn, advancedPwrControlEnValue).ConfigureAwait(false);
                    await client.WriteSingleRegister(SunspecConsts.ReactivePwrConfig, reactivePwrConfigValue).ConfigureAwait(false);

                    await CommitValues(client).ConfigureAwait(false);

                    var enableRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.AdvancedPwrControlEn).ConfigureAwait(false);
                    var configRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.ReactivePwrConfig).ConfigureAwait(false);

                    if (enableRead.Value != advancedPwrControlEnValue || configRead.Value != reactivePwrConfigValue)
                        throw new InvalidOperationException("Enabling advanced power control failed");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Enable advanced power control failed {ex.ToDetailedString()}");
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Commits written register values into the inverter logic.
        /// </summary>
        private async Task<bool> CommitValues(ModbusClient client)
        {
            await client.WriteSingleRegister(SunspecConsts.CommitPowerControlSettings, (ushort)1).ConfigureAwait(false);

            for (int i = 0; i < 30; i++)
            {
                var read = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.CommitPowerControlSettings).ConfigureAwait(false);

                if (read.Value == 0x00)
                {
                    _logger.LogInformation("Modbus commit succeeded");
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }

            throw new InvalidOperationException("Failed to commit values");
        }

        /// <summary>
        /// Restores the inverter to default power control settings.
        /// Note: the inverter restarts after this call (takes several minutes).
        /// </summary>
        public async Task RestoreDynamicPowerSettings(string id)
        {
            await ExecuteModbusAsync(async () =>
            {
                uint advancedPwrControlEnValue = 1;
                uint reactivePwrConfigValue = 0;
                ushort restoreValue = 1;

                try
                {
                    using var client = await GetModbusClient(id).ConfigureAwait(false);

                    await client.WriteSingleRegister(SunspecConsts.RestorePowerControlDefaultSettings, restoreValue).ConfigureAwait(false);

                    await CommitValues(client).ConfigureAwait(false);

                    var enableRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.AdvancedPwrControlEn).ConfigureAwait(false);
                    var configRead = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.Int32>(SunspecConsts.ReactivePwrConfig).ConfigureAwait(false);

                    if (enableRead.Value != advancedPwrControlEnValue || configRead.Value != reactivePwrConfigValue)
                        throw new InvalidOperationException("Restore advanced power settings failed");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Restore advanced power control settings failed {ex.ToDetailedString()}");
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the current active power limit percentage.
        /// </summary>
        public async Task<float> GetActivePowerLimit(string id)
        {
            return await ExecuteModbusAsync(async () =>
            {
                using var client = await GetModbusClient(id).ConfigureAwait(false);
                var result = await client.ReadHoldingRegisters<Djohnnie.SolarEdge.ModBus.TCP.Types.UInt16>(SunspecConsts.ActivePowerLimit).ConfigureAwait(false);
                return (float)result.Value;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets the active power limit percentage (0–100).
        /// Dynamic power mode must be enabled before calling this method.
        /// </summary>
        public async Task SetActivePowerLimit(string id, ushort power)
        {
            if (power > 100) throw new ArgumentOutOfRangeException(nameof(power));

            await ExecuteModbusAsync(async () =>
            {
                using var client = await GetModbusClient(id).ConfigureAwait(false);
                await client.WriteSingleRegister(SunspecConsts.ActivePowerLimit, power).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Stores the collected power data in the database for completed quarter hours.
        /// </summary>
        private async Task StoreData(Dictionary<string, Dictionary<DateTime, double>> collectedPowerData)
        {
            var now = _timeZoneService.Now;

            foreach (var collectionKeyValue in collectedPowerData)
            {
                var id = collectionKeyValue.Key;
                var collection = collectionKeyValue.Value;

                var quarterGroups = collection
                    .GroupBy(c => c.Key.DateFloorQuarter())
                    .OrderBy(g => g.Key)
                    .ToList();

                foreach (var quarterGroup in quarterGroups)
                {
                    var quarter = quarterGroup.Key;
                    var nextQuarter = quarter.AddMinutes(15);

                    // Only store completed quarters.
                    if (now < nextQuarter)
                        continue;

                    var values = quarterGroup
                        .Select(g => g.Value)
                        .Where(v => !double.IsNaN(v) && !double.IsInfinity(v))
                        .ToList();

                    if (values.Count < 1)
                        continue;

                    var entry = new SolarInverterData
                    {
                        ProviderName = ProviderName,
                        InverterId = id,
                        Time = quarter,
                        Power = values.Average()
                    };

                    await _solarEdgeDataService.Add(new List<SolarInverterData> { entry }).ConfigureAwait(false);

                    foreach (var item in quarterGroup)
                        collection.Remove(item.Key);
                }
            }
        }
    }
}
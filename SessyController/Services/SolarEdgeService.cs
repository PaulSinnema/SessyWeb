using Djohnnie.SolarEdge.ModBus.TCP;
using Djohnnie.SolarEdge.ModBus.TCP.Constants;
using Types = Djohnnie.SolarEdge.ModBus.TCP.Types;
using Microsoft.Extensions.Options;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Providers;
namespace SessyController.Services
{
    /// <summary>
    /// This class is used to read the result from the SolarEdge inverter.
    /// </summary>
    public class SolarEdgeService
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
        private readonly LoggingService<SolarEdgeService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly PowerSystemsConfig _powerSystemsConfig;
        private readonly TcpClientProvider _tcpClientProvider;

        public SolarEdgeService(IConfiguration configuration,
                                      LoggingService<SolarEdgeService> logger,
                                      IHttpClientFactory httpClientFactory,
                                      IOptions<PowerSystemsConfig> powerSystemsConfig,
                                      TcpClientProvider tcpClientProvide)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _powerSystemsConfig = powerSystemsConfig.Value;
            _tcpClientProvider = tcpClientProvide;
        }

        /// <summary>
        /// Get the AC power output from the inverter.
        /// </summary>
        /// <returns>Unscaled AC Power output</returns>
        public async Task<ushort> GetACPower()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var result = await client.ReadHoldingRegisters<Types.UInt16>(SunspecConsts.I_AC_Power);

            return result.Value;
        }

        /// <summary>
        /// Gets the scaling factor to be used to convert AC power to watts
        /// </summary>
        /// <returns>Scaling factor</returns>
        public async Task<short> GetACPowerScaleFactor()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var result = await client.ReadHoldingRegisters<Types.Int16>(SunspecConsts.I_AC_Power_SF);

            return result.Value;
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// </summary>
        public async Task<ushort> GetStatus()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var result = await client.ReadHoldingRegisters<Types.UInt16>(SunspecConsts.I_Status);

            return result.Value;
        }

        public async Task EnableDynamicPower()
        {
            try
            {
                using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

                await client.WriteSingleRegister(SunspecConsts.AdvancedPwrControlEn, (UInt32)1);
                await client.WriteSingleRegister(SunspecConsts.ReactivePwrConfig, (UInt32)4);

                await CommitValues(client);

                var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Types.UInt32>(SunspecConsts.AdvancedPwrControlEn);
                var reactivePwrConfigRead = await client.ReadHoldingRegisters<Types.UInt32>(SunspecConsts.ReactivePwrConfig);

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

            await client.WriteSingleRegister(SunspecConsts.CommitPowerControlSettings, 1);

            for (int i = 0; i < 30; i++)
            {
                var read = await client.ReadHoldingRegisters<Types.UInt16>(SunspecConsts.CommitPowerControlSettings);

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

        public async Task RestoreDynamicPowerSettings()
        {
            UInt32 AdvancedPwrControlEnValue = 0;
            UInt32 ReactivePwrConfigValue = 0;
           // UInt16 RestorePowerControlDefaultSettingsValue = 1;

            try
            {
                using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

                await client.WriteSingleRegister(SunspecConsts.AdvancedPwrControlEn, AdvancedPwrControlEnValue);
                await client.WriteSingleRegister(SunspecConsts.ReactivePwrConfig, ReactivePwrConfigValue);
                // await client.WriteSingleRegister(SunspecConsts.RestorePowerControlDefaultSettings, RestorePowerControlDefaultSettingsValue);

                await CommitValues(client);

                var enableDynamicPowerControlRead = await client.ReadHoldingRegisters<Types.UInt32>(SunspecConsts.AdvancedPwrControlEn);
                var reactivePwrConfigRead = await client.ReadHoldingRegisters<Types.UInt32>(SunspecConsts.ReactivePwrConfig);

                if (enableDynamicPowerControlRead.Value != AdvancedPwrControlEnValue ||
                    reactivePwrConfigRead.Value != ReactivePwrConfigValue)
                    throw new InvalidOperationException($"Restore advanced power settings failed");
               // var RestorePowerControlDefaultSettingsRead = await client.ReadHoldingRegisters<Types.UInt16>(SunspecConsts.RestorePowerControlDefaultSettings);

                //if (RestorePowerControlDefaultSettingsRead.Value != 0) throw new InvalidOperationException($"Restoring default values failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Restore advanced power control settings failed {ex.ToDetailedString()}");
            }
        }

        public async Task<ushort> GetCosPhiQPreference()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var preference = await client.ReadHoldingRegisters<Types.UInt16>(cosPhiQPreference);

            return preference.Value;
        }

        public async Task<ushort> GetActiveReactivePreference()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var preference = await client.ReadHoldingRegisters<Types.UInt16>(activeReactivePreference);

            return preference.Value;
        }

        public async Task<float> GetActivePowerLimit()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var powerSet = await client.ReadHoldingRegisters<Types.Float32>(activePowerLimit);

            return powerSet.Value;
        }

        public async Task<float> GetReactivePowerLimit()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var powerSet = await client.ReadHoldingRegisters<Types.Float32>(reactivePowerLimit);

            return powerSet.Value;
        }

        public async Task<float> GetDynamicActivePowerLimit()
        {
            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            var powerSet = await client.ReadHoldingRegisters<Types.Float32>(dynamicActivePowerLimit);

            return powerSet.Value;
        }

        public async Task SetActivePowerLimit(UInt16 power)
        {
            if (power < 0 || power > 100) throw new ArgumentOutOfRangeException(nameof(power));

            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            await client.WriteSingleRegister(SunspecConsts.ActivePowerLimit, power);
        }

        public async Task SetReactivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            await client.WriteSingleRegister(reactivePowerLimit, power);
        }

        public async Task SetDynamicActivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            await client.WriteSingleRegister(dynamicActivePowerLimit, power);
        }

        public async Task SetDynamicReactivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            using var client = await _tcpClientProvider.GetModbusClient("SolarEdge");

            await client.WriteSingleRegister(dynamicReactivePowerLimit, power);
        }
    }
}


using Microsoft.Extensions.Options;
using NModbus;
using SessyCommon.Converters;
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
            Configurations.Endpoint endpoint = GetEndpointConfig();

            ushort[] result = await ReadHoldingRegisters(endpoint.SlaveId, acPowerStartAddress, 1);

            return result[0];
        }

        /// <summary>
        /// Gets the scaling factor to be used to convert AC power to watts
        /// </summary>
        /// <returns>Scaling factor</returns>
        public async Task<ushort> GetACPowerScaleFactor()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            ushort[] result = await ReadHoldingRegisters(endpoint.SlaveId, acPowerSFStartAddress, 1);

            return result[0];
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// </summary>
        public async Task<ushort> GetStatus()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            ushort[] result = await ReadHoldingRegisters(endpoint.SlaveId, statusStartAddress, 1);

            return result[0];
        }

        public async Task EnableDynamicPower()
        {
            try
            {
                Configurations.Endpoint endpoint = GetEndpointConfig();

                await WriteUshortToRegister(endpoint.SlaveId, enableDynamicPowerControl, 1);
                await WriteUintToRegisters(endpoint.SlaveId, advancedPwrControlEn, 1);
                await WriteUintToRegisters(endpoint.SlaveId, reactivePwrConfig, 4);

                await CommitValues(endpoint);

                var enableDynamicPowerControlRead = await ReadUshortFromRegister(endpoint.SlaveId, enableDynamicPowerControl);
                var advancePowerControlRead = await ReadUintFromRegisters(endpoint.SlaveId, advancedPwrControlEn);
                var reactivePwrConfigRead = await ReadUintFromRegisters(endpoint.SlaveId, reactivePwrConfig);

                if (enableDynamicPowerControlRead != 1 || advancePowerControlRead != 1 || reactivePwrConfigRead != 4)
                    throw new InvalidOperationException($"Enabling advanced power control failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Enable advanced power control failed {ex.ToDetailedString()}");
            }

        }

        private async Task<bool> CommitValues(Configurations.Endpoint endpoint)
        {
            const ushort commitChanges = 0xF100;
            ushort result = 0;

            await WriteUshortToRegister(endpoint.SlaveId, commitChanges, 1);

            for (int i = 0; i < 30; i++)
            {
                result = await ReadUshortFromRegister(endpoint.SlaveId, commitChanges);

                if (result == 0x00)
                {
                    _logger.LogInformation("Modbus commit succeeded");
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            throw new InvalidOperationException($"Failed to commit values, last error code: {result}");
        }

        public async Task RestoreDynamicPowerSettings()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            await WriteUshortToRegister(endpoint.SlaveId, RestorePowerControlDefaultSettings, 1);

            await CommitValues(endpoint);

            var RestorePowerControlDefaultSettingsRead = await ReadUshortFromRegister(endpoint.SlaveId, RestorePowerControlDefaultSettings);

            if (RestorePowerControlDefaultSettingsRead != 0) throw new InvalidOperationException($"Restoring default values failed");
        }

        public async Task<ushort> GetCosPhiQPreference()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            ushort preference = await ReadUshortFromRegister(endpoint.SlaveId, cosPhiQPreference);

            return preference;
        }

        public async Task<ushort> GetActiveReactivePreference()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            ushort preference = await ReadUshortFromRegister(endpoint.SlaveId, activeReactivePreference);

            return preference;
        }

        public async Task<float> GetActivePowerLimit()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            float powerSet = await ReadFloatFromRegisters(endpoint.SlaveId, activePowerLimit, false);

            return powerSet;
        }

        public async Task<float> GetReactivePowerLimit()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            float powerSet = await ReadFloatFromRegisters(endpoint.SlaveId, reactivePowerLimit, false);

            return powerSet;
        }

        public async Task<float> GetDynamicActivePowerLimit()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            float powerSet = await ReadFloatFromRegisters(endpoint.SlaveId, dynamicActivePowerLimit, false);

            return powerSet;
        }

        public async Task SetActivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            await WriteFloatValueToAddress(activePowerLimit, power);
        }

        public async Task SetReactivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            await WriteFloatValueToAddress(reactivePowerLimit, power);
        }

        public async Task SetDynamicActivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            await WriteFloatValueToAddress(dynamicActivePowerLimit, power);
        }

        public async Task SetDynamicReactivePowerLimit(float power)
        {
            if (power < 0) throw new ArgumentOutOfRangeException(nameof(power));

            await WriteFloatValueToAddress(dynamicReactivePowerLimit, power);
        }

        private async Task WriteFloatValueToAddress(ushort address, float value)
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            await WriteFloatToRegisters(endpoint.SlaveId, address, value, false);

            await CommitValues(endpoint);

            float powerSet = await ReadFloatFromRegisters(endpoint.SlaveId, address, false);

            if (powerSet != value) throw new InvalidOperationException($"Setting active power limit failed");
        }

        private async Task<float> ReadFloatFromRegisters(byte slaveId, ushort address = 0, bool isBigEndian = false)
        {
            var results = await ReadHoldingRegisters(slaveId, address, 2);

            var result = ModbusHelper.RegistersToFloat(results, isBigEndian);

            return result;
        }

        private async Task<uint> ReadUintFromRegisters(byte slaveId, ushort address = 0, bool isBigEndian = false)
        {
            var results = await ReadHoldingRegisters(slaveId, address, 2);

            var result = ModbusHelper.RegistersToUInt(results, isBigEndian);

            return result;
        }

        private async Task<ushort> ReadUshortFromRegister(byte slaveId, ushort address = 0)
        {
            var results = await ReadHoldingRegisters(slaveId, address, 1);

            return results[0];
        }

        /// <summary>
        /// Read holding registers function code 3.
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfRegisters"></param>
        /// <returns>List of ushort values.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task<ushort[]> ReadHoldingRegisters(byte slaveId, ushort startAddress = 0, ushort numberOfRegisters = 1)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                Configurations.Endpoint endpoint = GetEndpointConfig();

                // Read holding registers (Function Code 0x03)
                ushort[] registers = await master.ReadHoldingRegistersAsync(slaveId, startAddress, numberOfRegisters);

                return registers;
            }
            catch (Exception ex)
            {
                var message = $"Error getting SolarEdge data: start address: {startAddress:X}, number of registers: {numberOfRegisters}";

                _logger.LogError(ex.ToDetailedString(message));

                throw new InvalidOperationException(message, ex);
            }
        }

        private async Task WriteUshortToRegister(byte slaveId, ushort startAddress = 0, ushort value = 0)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                // Write holding registers (Function Code 0x03)
                await master.WriteSingleRegisterAsync(slaveId, startAddress, value);
            }
            catch (Exception ex)
            {
                var message = $"Error writing SolarEdge (ushort) data: start address: 0x{startAddress:X}";

                _logger.LogError(ex.ToDetailedString(message));

                throw new InvalidOperationException(message, ex);
            }
        }

        private async Task WriteUintToRegisters(byte slaveId, ushort startAddress = 0, uint value = 0, bool isBigEndian = false)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory();
                using var master = factory.CreateMaster(tcpClient);

                var data = ModbusHelper.ConvertUIntToRegisters(value, isBigEndian);

                await master.WriteMultipleRegistersAsync(slaveId, startAddress++, data);
            }
            catch (Exception ex)
            {
                var message = $"Error writing SolarEdge (uint) data: start address: 0x{startAddress:X}";

                _logger.LogError(ex.ToDetailedString(message));

                throw new InvalidOperationException(message, ex);
            }
        }

        /// <summary>
        /// Write registers function code 3.
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfRegisters"></param>
        /// <returns>List of ushort values.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task WriteFloatToRegisters(byte slaveId, ushort startAddress = 0, float value = 0.0f, bool isBigEndian = false)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                var valuesToWrite = ModbusHelper.ConvertFloatToRegisters(value, isBigEndian);

                await master.WriteMultipleRegistersAsync(slaveId, startAddress, valuesToWrite);
            }
            catch (Exception ex)
            {
                var message = $"Error writing SolarEdge data: start address: {startAddress:X}";

                _logger.LogError(ex.ToDetailedString(message));

                throw new InvalidOperationException(message, ex);
            }
        }

        private Configurations.Endpoint GetEndpointConfig()
        {
            if (!_powerSystemsConfig.Endpoints.TryGetValue("SolarEdge", out var endpoint))
            {
                var message = "SolarEdge configuration is missing";

                _logger.LogError(message);

                throw new InvalidOperationException(message);
            }

            return endpoint;
        }
    }
}


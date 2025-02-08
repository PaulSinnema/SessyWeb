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

        private const ushort commitChanges = 0xF100;
        private const ushort RestorePowerControlDefaultSettings = 0xF101;

        private const ushort activePowerLimit = 0xF322; //0xF30C; //0xF001;

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

                await WriteSingleRegister(endpoint.SlaveId, enableDynamicPowerControl, 1);
                //await WriteSingleRegister(endpoint.SlaveId, advancedPwrControlEn, (uint)1);
                //await WriteSingleRegister(endpoint.SlaveId, reactivePwrConfig, (uint)4);
                await WriteSingleRegister(endpoint.SlaveId, commitChanges, 1);

                var results = await ReadHoldingRegisters(endpoint.SlaveId, enableDynamicPowerControl, 1);

                if (results[0] != 1) throw new InvalidOperationException($"Enabling dynamic power failed");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Enable dynamic power failed {ex.ToDetailedString()}");
            }

        }

        public async Task RestoreDynamicPowerSettings()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            await WriteSingleRegister(endpoint.SlaveId, RestorePowerControlDefaultSettings, (uint)1);
            await WriteSingleRegister(endpoint.SlaveId, commitChanges, 1);

            var results = await ReadHoldingRegisters(endpoint.SlaveId, enableDynamicPowerControl, 1);
            if (results[0] != 0) throw new InvalidOperationException($"Restoring dynamic power failed");
        }

        public async Task<float> GetActivePowerLimit()
        {
            Configurations.Endpoint endpoint = GetEndpointConfig();

            ushort[] results = await ReadHoldingRegisters(endpoint.SlaveId, activePowerLimit, 1);

            var powerSet = ModbusHelper.RegistersToFloat(results);

            return powerSet;
        }

        public async Task SetActivePowerLimit(float power)
        {
            if (power < 0 || power > 100) throw new ArgumentOutOfRangeException(nameof(power));

            Configurations.Endpoint endpoint = GetEndpointConfig();

            await WriteFloatToRegisters(endpoint.SlaveId, activePowerLimit, power);
            await WriteSingleRegister(endpoint.SlaveId, commitChanges, 1);

            float powerSet = await ReadFloatFromRegisters(endpoint);

            if (powerSet != power) throw new InvalidOperationException($"Setting max power failed");
        }

        private async Task<float> ReadFloatFromRegisters(Configurations.Endpoint endpoint)
        {
            var results = await ReadHoldingRegisters(endpoint.SlaveId, activePowerLimit, 2);

            var powerSet = ModbusHelper.RegistersToFloat(results);

            return powerSet;
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
                var message = $"Error getting SolarEdge data: start address: {startAddress}, number of registers: {numberOfRegisters}";

                _logger.LogError(ex.ToDetailedString(message));

                throw new InvalidOperationException(message, ex);
            }
        }

        private async Task WriteSingleRegister(byte slaveId, ushort startAddress = 0, ushort value = 0)
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

        private async Task WriteSingleRegister(byte slaveId, ushort startAddress = 0, uint value = 0)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                var bytes = BitConverter.GetBytes(value);
                var data = new ushort[2] { BitConverter.ToUInt16(bytes, 0), BitConverter.ToUInt16(bytes, 2) };

                // Write holding registers (Function Code 0x03)
                await master.WriteMultipleRegistersAsync(slaveId, startAddress, data);
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
        private async Task WriteFloatToRegisters(byte slaveId, ushort startAddress = 0, float value = 0.0f, bool isBigEndian = true)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                var valuesToWrite = ModbusHelper.ConvertFloatToRegisters(value, isBigEndian);

                // Write holding registers (Function Code 0x03)
                await master.WriteMultipleRegistersAsync(slaveId, startAddress, valuesToWrite);
            }
            catch (Exception ex)
            {
                var message = $"Error writing SolarEdge data: start address: {startAddress}";

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

        /// <summary>
        /// The NModbus library always returns ushort[] array but the values can be negative sometimes, so we need to convert to short[].
        /// </summary>
        /// <param name="array">Array of ushort values from the registers</param>
        /// <returns>Array of short[] values from the registers.</returns>
        private short[] ConvertUnsignedShortArrayToSignedShortArray(ushort[] array)
        {
            // Maak een nieuwe short[] array van dezelfde lengte
            short[] signedArray = new short[array.Length];

            // Converteer elk ushort-element naar short
            for (int i = 0; i < array.Length; i++)
            {
                signedArray[i] = unchecked((short)array[i]);
            }

            return signedArray;
        }
    }
}


using Microsoft.Extensions.Options;
using NModbus;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Interfaces;
using SessyController.Providers;

namespace SessyController.Services
{
    /// <summary>
    /// This class is used to read the result from the SolarEdge inverter.
    /// </summary>
    public class SolarEdgeService : ISolarSystemInterface
    {
        private const ushort acPowerStartAddress = 0x9C93;
        private const ushort acPowerSFStartAddress = 0x9C94;
        private const ushort statusStartAddress = 0x9CAB;

        private const ushort enableDynamicPowerControl = 0xF300;
        private const ushort advancedPwrControlEn = 0xF142;
        private const ushort reactivePwrConfig = 0xF104;

        private const ushort commitChanges = 0xF100;
        private const ushort RestorePowerControlDefaultSettings = 0xF101;

        private const ushort activePowerLimit = 0xF001;

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
        public async Task<short> GetACPower()
        {
            short[] result = await ReadHoldingRegisters(acPowerStartAddress, 1);

            return result[0];
        }

        /// <summary>
        /// Gets the scaling factor to be used to convert AC power to watts
        /// </summary>
        /// <returns>Scaling factor</returns>
        public async Task<short> GetACPowerScaleFactor()
        {
            short[] result = await ReadHoldingRegisters(acPowerSFStartAddress, 1);

            return result[0];
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// </summary>
        public async Task<short> GetStatus()
        {
            short[] result = await ReadHoldingRegisters(statusStartAddress, 1);

            return result[0];
        }

        public short[] GetArray(Int32 value)
        {
            int valueToWrite = value; // Voorbeeldwaarde
            short highOrderValue = (short)(valueToWrite >> 16); // Hoogste 16 bits
            short lowOrderValue = (short)(valueToWrite & 0xFFFF); // Laagste 16 bits
            return new short[] { highOrderValue, lowOrderValue };
        }

        public async Task EnableDynamicPower()
        {
            await WriteSingleRegister(enableDynamicPowerControl, 1);
            var array = GetArray(1);
            await WriteMultipleRegisters(advancedPwrControlEn, (ushort)array.Length, array);
            array = GetArray(4);
            await WriteMultipleRegisters(reactivePwrConfig, (ushort)array.Length, array);
            await WriteSingleRegister(commitChanges, 1);

            var results = await ReadHoldingRegisters(advancedPwrControlEn, 1);

            if (results[0] != 0) throw new InvalidOperationException($"Enabling dynamic power failed");
        }

        public async Task RestoreDynamicPowerSettings()
        {
            await WriteMultipleRegisters(RestorePowerControlDefaultSettings, 1, new short[] { 1 });

            var results = await ReadHoldingRegisters(advancedPwrControlEn, 1);

            if (results[0] != 0) throw new InvalidOperationException($"Enabling dynamic power failed");
        }

        public async Task<short> GetActivePowerLimit()
        {
            short[] result = await ReadHoldingRegisters(activePowerLimit, 1);

            return result[0];
        }

        public async Task SetActivePowerLimit(short power)
        {
            if (power < 0 || power > 100) throw new ArgumentOutOfRangeException(nameof(power));

            await WriteMultipleRegisters(activePowerLimit, 1, new short[1] { power });
        }

        /// <summary>
        /// Read holding registers function code 3.
        /// </summary>
        /// <param name="startAddress"></param>
        /// <param name="numberOfRegisters"></param>
        /// <returns>List of ushort values.</returns>
        /// <exception cref="InvalidOperationException"></exception>
        private async Task<short[]> ReadHoldingRegisters(ushort startAddress = 0, ushort numberOfRegisters = 1)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                Configurations.Endpoint endpoint = GetEndpointConfig();

                var slaveId = endpoint.SlaveId;

                // Read holding registers (Function Code 0x03)
                ushort[] registers = await master.ReadHoldingRegistersAsync(slaveId, startAddress, numberOfRegisters);

                return ConvertUnsignedShortArrayToSignedShortArray(registers);
            }
            catch (Exception ex)
            {
                var message = $"Error getting SolarEdge data: start address: {startAddress}, number of registers: {numberOfRegisters}";

                _logger.LogError(ex.ToDetailedString(message));

                throw new InvalidOperationException(message, ex);
            }
        }

        private async Task WriteSingleRegister(ushort startAddress = 0, short value = 0)
        {
            try
            {
                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                Configurations.Endpoint endpoint = GetEndpointConfig();

                var slaveId = endpoint.SlaveId;

                ushort valueToWrite = unchecked((ushort)value);

                // Write holding registers (Function Code 0x03)
                await master.WriteSingleRegisterAsync(slaveId, startAddress, valueToWrite);
            }
            catch (Exception ex)
            {
                var message = $"Error writing SolarEdge data: start address: 0x{startAddress:X}";

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
        private async Task WriteMultipleRegisters(ushort startAddress = 0, ushort numberOfRegisters = 1, short[]? values = null)
        {
            try
            {
                if (values == null) throw new ArgumentNullException(nameof(values));

                var tcpClient = _tcpClientProvider.GetTcpClient("SolarEdge");

                // Create a Modbus master
                var factory = new ModbusFactory(null, true);
                using var master = factory.CreateMaster(tcpClient);

                Configurations.Endpoint endpoint = GetEndpointConfig();

                var slaveId = endpoint.SlaveId;

                var toWriteArray = ConvertSignedShortArrayToUnSignedShortArray(values);

                // Write holding registers (Function Code 0x03)
                await master.WriteMultipleRegistersAsync(slaveId, startAddress, toWriteArray);
            }
            catch (Exception ex)
            {
                var message = $"Error writing SolarEdge data: start address: {startAddress}, number of registers: {numberOfRegisters}";

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

        /// <summary>
        /// The NModbus library always returns ushort[] array but the values can be negative sometimes, so we need to convert to short[].
        /// </summary>
        /// <param name="array">Array of ushort values from the registers</param>
        /// <returns>Array of short[] values from the registers.</returns>
        private ushort[] ConvertSignedShortArrayToUnSignedShortArray(short[] array)
        {
            // Maak een nieuwe short[] array van dezelfde lengte
            ushort[] unsignedArray = new ushort[array.Length];

            // Converteer elk ushort-element naar short
            for (int i = 0; i < array.Length; i++)
            {
                unsignedArray[i] = unchecked((ushort)array[i]);
            }

            return unsignedArray;
        }
    }
}


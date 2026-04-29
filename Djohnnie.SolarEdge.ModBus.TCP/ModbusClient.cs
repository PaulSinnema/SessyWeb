using Djohnnie.SolarEdge.ModBus.TCP.Constants;
using Djohnnie.SolarEdge.ModBus.TCP.Converters;
using Djohnnie.SolarEdge.ModBus.TCP.Types;
using NModbus;
using System.Net.Sockets;

namespace Djohnnie.SolarEdge.ModBus.TCP;

public interface IModbusClient : IDisposable
{
    public Task Connect();
    public void Disconnect();
}

public class ModbusClient : IModbusClient
{
    private const byte UNIT_IDENTIFIER = 1;

    private TcpClient? _client;
    private IModbusMaster? _master;
    private bool _isConnected;

    public string IpAddress { get; init; }
    public int Port { get; init; }
    public int ConnectionTimeout { get; init; }
    public bool IsConnected => _isConnected;

    public ModbusClient(string ipAddress, int port, int connectionTimeout = 5000)
    {
        IpAddress = ipAddress;
        Port = port;
        ConnectionTimeout = connectionTimeout;
    }

    public async Task Connect()
    {
        if (!_isConnected)
        {
            _client = new TcpClient()
            {
                ReceiveTimeout = ConnectionTimeout,
                SendTimeout = ConnectionTimeout
            };
            await _client.ConnectAsync(IpAddress, Port);
            var factory = new ModbusFactory();
            _master = factory.CreateMaster(_client);
            _isConnected = true;
        }
    }

    public void Disconnect()
    {
        if (_isConnected)
        {
            _client!.Dispose();
            _master!.Dispose();
            _isConnected = false;
        }
    }

    public async Task<TResult> ReadHoldingRegisters<TResult>(ushort address) where TResult : ModbusType, new()
    {
        var sunspecDefinition = SunspecConsts.SunspecDefinitions[address];

        if (sunspecDefinition == null) throw new InvalidOperationException($"Cannot find sunspec definition for {address}");

        var result = new TResult()
        {
            Address = address,
            Name = sunspecDefinition.Name!,
            Description = sunspecDefinition.Description!
        };

        var data = await _master!.ReadHoldingRegistersAsync(UNIT_IDENTIFIER, address, result.Size);
        result.SetValue(data);

        return result;
    }

    /// <summary>
    /// Reads two consecutive holding registers in a single Modbus transaction,
    /// preventing race conditions between value and scale factor reads.
    ///
    /// Both addresses must be consecutive (address2 = address1 + size1) and
    /// each register must be exactly 1 register wide (Size = 1).
    ///
    /// Example: ReadHoldingRegistersBlock&lt;Int16, Int16&gt;(I_AC_Power, I_AC_Power_SF)
    /// reads both registers atomically so power and scale factor are always consistent.
    /// </summary>
    public async Task<(TResult1 first, TResult2 second)> ReadHoldingRegistersBlock<TResult1, TResult2>(
        ushort address1,
        ushort address2)
        where TResult1 : ModbusType, new()
        where TResult2 : ModbusType, new()
    {
        var def1 = SunspecConsts.SunspecDefinitions[address1];
        var def2 = SunspecConsts.SunspecDefinitions[address2];

        if (def1 == null) throw new InvalidOperationException($"Cannot find sunspec definition for {address1}");
        if (def2 == null) throw new InvalidOperationException($"Cannot find sunspec definition for {address2}");

        var result1 = new TResult1
        {
            Address = address1,
            Name = def1.Name!,
            Description = def1.Description!
        };

        var result2 = new TResult2
        {
            Address = address2,
            Name = def2.Name!,
            Description = def2.Description!
        };

        // Read both registers in a single Modbus transaction.
        ushort totalSize = (ushort)(result1.Size + result2.Size);
        var data = await _master!.ReadHoldingRegistersAsync(UNIT_IDENTIFIER, address1, totalSize);

        // Split the raw data and set values on each result.
        result1.SetValue(data.Take(result1.Size).ToArray());
        result2.SetValue(data.Skip(result1.Size).Take(result2.Size).ToArray());

        return (result1, result2);
    }

    public async Task WriteSingleRegister(ushort address, ushort value)
    {
        await _master!.WriteSingleRegisterAsync(UNIT_IDENTIFIER, address, value);
    }

    public async Task WriteSingleRegister(ushort address, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        var data = new ushort[2] { BitConverter.ToUInt16(bytes, 0), BitConverter.ToUInt16(bytes, 2) };
        await _master!.WriteMultipleRegistersAsync(UNIT_IDENTIFIER, address, data);
    }

    public async Task WriteMultipleRegisters(ushort address, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        var data = new ushort[2] { BitConverter.ToUInt16(bytes, 0), BitConverter.ToUInt16(bytes, 2) };
        await _master!.WriteMultipleRegistersAsync(UNIT_IDENTIFIER, address, data);
    }

    public void Dispose()
    {
        if (_isConnected)
        {
            Disconnect();
        }
    }
}
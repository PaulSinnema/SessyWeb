using NModbus.Utility;
using System.Runtime.InteropServices;

namespace Djohnnie.SolarEdge.ModBus.TCP.Converters;

internal static class NumericConverter
{
    internal static void SetValue(this Types.UInt16 result, Span<ushort> data)
    {
        result.SetValue(data[0]);
    }

    internal static void SetValue(this Types.Int16 result, Span<ushort> data)
    {
        Span<short> shortData = MemoryMarshal.Cast<ushort, short>(data);
        result.SetValue(shortData[0]);
    }

    internal static void SetValue(this Types.Acc32 result, Span<ushort> data)
    {
        var value = ModbusUtility.GetUInt32(data[0], data[1]);
        result.SetValue(value);
    }

    internal static void SetValue(this Types.UInt32 result, Span<ushort> data)
    {
        var value = ModbusUtility.GetUInt32(data[1], data[0]);
        result.SetValue(value);
    }

    internal static void SetValue(this Types.Int32 result, Span<ushort> data)
    {
        Span<int> intData = MemoryMarshal.Cast<ushort, int>(data);
        result.SetValue(intData[0]);
    }

    internal static void SetValue(this Types.UInt64 result, Span<ushort> data)
    {
        Span<ulong> ulongData = MemoryMarshal.Cast<ushort, ulong>(data);
        result.SetValue(ulongData[0]);
    }

    internal static void SetValue(this Types.Float32 result, Span<ushort> data)
    {
        var value = ModbusUtility.GetSingle(data[1], data[0]);
        result.SetValue(value);
    }

    public static ulong GetUInt32(ushort highOrderValue, ushort lowOrderValue)
    {
        byte[] value = BitConverter.GetBytes(lowOrderValue)
            .Concat(BitConverter.GetBytes(highOrderValue))
            .ToArray();

        return BitConverter.ToUInt64(value, 0);
    }
}
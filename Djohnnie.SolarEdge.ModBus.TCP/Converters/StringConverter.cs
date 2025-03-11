using Djohnnie.SolarEdge.ModBus.TCP.Types;
using System.Runtime.InteropServices;
using System.Text;

namespace Djohnnie.SolarEdge.ModBus.TCP.Converters;

internal static class StringConverter
{
    internal static void SetValue(this String32 result, Span<ushort> data)
    {
        SwitchEndianness(data);
        var bytes = MemoryMarshal.Cast<ushort, byte>(data);
        result.SetValue(Encoding.ASCII.GetString(bytes));
    }

    internal static void SetValue(this String16 result, Span<ushort> data)
    {
        SwitchEndianness(data);
        var bytes = MemoryMarshal.Cast<ushort, byte>(data);
        result.SetValue(Encoding.ASCII.GetString(bytes));
    }

    public static void SwitchEndianness<T>(Span<T> dataset) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var dataset_bytes = MemoryMarshal.Cast<T, byte>(dataset);

        for (int i = 0; i < dataset_bytes.Length; i += size)
        {
            for (int j = 0; j < size / 2; j++)
            {
                var i1 = i + j;
                var i2 = i - j + size - 1;

                (dataset_bytes[i2], dataset_bytes[i1]) = (dataset_bytes[i1], dataset_bytes[i2]);
            }
        }
    }
}
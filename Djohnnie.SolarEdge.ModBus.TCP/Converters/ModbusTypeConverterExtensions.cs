using Djohnnie.SolarEdge.ModBus.TCP.Types;

namespace Djohnnie.SolarEdge.ModBus.TCP.Converters;

internal static class ModbusTypeConverterExtensions
{
    internal static void SetValue<TType>(this TType result, ushort[] data) where TType : ModbusType
    {
        switch (result)
        {
            case Types.UInt16 uint16:
                uint16.SetValue(data.AsSpan());
                break;
            case Types.Int16 int16:
                int16.SetValue(data.AsSpan());
                break;
            case Types.UInt32 uint32:
                uint32.SetValue(data.AsSpan());
                break;
            case Types.Int32 int32:
                int32.SetValue(data.AsSpan());
                break;
            case Types.UInt64 uint64:
                uint64.SetValue(data.AsSpan());
                break;
            case Acc32 acc32:
                acc32.SetValue(data.AsSpan());
                break;
            case Float32 float32:
                float32.SetValue(data.AsSpan());
                break;
            case String16 string16:
                string16.SetValue(data.AsSpan());
                break;
            case String32 string32:
                string32.SetValue(data.AsSpan());
                break;
            default:
                throw new NotImplementedException("Type not implemented");
        }
    }
}
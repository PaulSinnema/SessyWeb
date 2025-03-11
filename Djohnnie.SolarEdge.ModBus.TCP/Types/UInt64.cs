namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class UInt64 : ModbusType
{
    private ulong _value;

    public override ushort Size => 4;

    public ulong Value => _value;

    internal void SetValue(ulong value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return $"{Value}";
    }
}
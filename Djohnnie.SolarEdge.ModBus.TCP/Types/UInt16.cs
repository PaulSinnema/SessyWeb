namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class UInt16 : ModbusType
{
    private ushort _value;

    public override ushort Size => 1;

    public ushort Value => _value;

    internal void SetValue(ushort value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return $"{Value}";
    }
}
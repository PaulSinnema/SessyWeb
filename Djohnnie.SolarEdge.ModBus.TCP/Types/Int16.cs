namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class Int16 : ModbusType
{
    private short _value;

    public override ushort Size => 1;

    public short Value => _value;

    internal void SetValue(short value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return $"{Value}";
    }
}
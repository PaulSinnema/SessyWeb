namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class Int32 : ModbusType
{
    private int _value;

    public override ushort Size => 2;

    public int Value => _value;

    internal void SetValue(int value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return $"{Value}";
    }
}
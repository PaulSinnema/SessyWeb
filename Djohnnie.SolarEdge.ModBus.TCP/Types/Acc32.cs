namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class Acc32 : ModbusType
{
    private uint _value;

    public override ushort Size => 2;

    public uint Value => _value;

    internal void SetValue(uint value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return $"{Value}";
    }
}
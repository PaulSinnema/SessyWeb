namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class String16 : ModbusType
{
    private string? _value;

    public override ushort Size => 8;

    public string Value => _value = string.Empty;

    internal void SetValue(string value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return Value;
    }
}
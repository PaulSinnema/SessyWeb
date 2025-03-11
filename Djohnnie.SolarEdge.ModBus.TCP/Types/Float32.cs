namespace Djohnnie.SolarEdge.ModBus.TCP.Types;

public class Float32 : ModbusType
{
    private float _value;

    public override ushort Size => 2;

    public float Value => _value;

    internal void SetValue(float value)
    {
        _value = value;
    }

    public override string ToString()
    {
        return $"{Value}";
    }
}
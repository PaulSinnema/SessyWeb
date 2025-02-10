namespace SessyCommon.Converters
{
    public class ModbusHelper
    {
        public static ushort[] ConvertFloatToRegisters(float value, bool isBigEndian = true)
        {
            // Converteer de float naar een array van 4 bytes
            byte[] bytes = BitConverter.GetBytes(value);

            // Controleer of de systeemendianness overeenkomt met de gewenste endianness
            if (BitConverter.IsLittleEndian == isBigEndian)
            {
                // Als de endianness niet overeenkomt, de bytevolgorde omkeren
                Array.Reverse(bytes);
            }

            // Combineer de bytes in twee 16-bits unsigned integers
            ushort[] registers = new ushort[2];
            registers[0] = BitConverter.ToUInt16(bytes, 0); // Hoog woord
            registers[1] = BitConverter.ToUInt16(bytes, 2); // Laag woord

            return registers;
        }

        public static float RegistersToFloat(ushort[] registers, bool isBigEndian = true)
        {
            if (registers == null || registers.Length != 2)
                throw new ArgumentException("De registers-array moet precies 2 elementen bevatten.");

            // Converteer de 16-bits registers naar een array van 4 bytes
            byte[] bytes = new byte[4];

            // Kopieer de waarden van de registers naar de bytes-array
            BitConverter.GetBytes(registers[0]).CopyTo(bytes, 0);
            BitConverter.GetBytes(registers[1]).CopyTo(bytes, 2);

            // Controleer of de systeemendianness overeenkomt met de gewenste endianness
            if (BitConverter.IsLittleEndian == isBigEndian)
            {
                // Als de endianness niet overeenkomt, de bytevolgorde omkeren
                Array.Reverse(bytes);
            }

            // Converteer de bytes terug naar een float
            return BitConverter.ToSingle(bytes, 0);
        }

        public static ushort[] ConvertUIntToRegisters(uint value, bool isBigEndian = true)
        {
            // Converteer de uint naar een array van 4 bytes
            byte[] bytes = BitConverter.GetBytes(value);

            // Controleer of de systeemendianness overeenkomt met de gewenste endianness
            if (BitConverter.IsLittleEndian == isBigEndian)
            {
                // Als de endianness niet overeenkomt, de bytevolgorde omkeren
                Array.Reverse(bytes);
            }

            // Combineer de bytes in twee 16-bits unsigned integers
            ushort[] registers = new ushort[2];
            registers[0] = BitConverter.ToUInt16(bytes, 0); // Hoog woord
            registers[1] = BitConverter.ToUInt16(bytes, 2); // Laag woord

            return registers;
        }

        public static uint RegistersToUInt(ushort[] registers, bool isBigEndian = true)
        {
            if (registers == null || registers.Length != 2)
                throw new ArgumentException("De registers-array moet precies 2 elementen bevatten.");

            // Converteer de 16-bits registers naar een array van 4 bytes
            byte[] bytes = new byte[4];

            // Kopieer de waarden van de registers naar de bytes-array
            BitConverter.GetBytes(registers[0]).CopyTo(bytes, 0);
            BitConverter.GetBytes(registers[1]).CopyTo(bytes, 2);

            // Controleer of de systeemendianness overeenkomt met de gewenste endianness
            if (BitConverter.IsLittleEndian == isBigEndian)
            {
                // Als de endianness niet overeenkomt, de bytevolgorde omkeren
                Array.Reverse(bytes);
            }

            // Converteer de bytes terug naar een uint
            return BitConverter.ToUInt32(bytes, 0);
        }

    }
}

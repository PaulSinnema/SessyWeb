using Djohnnie.SolarEdge.ModBus.TCP.Types;

namespace Djohnnie.SolarEdge.ModBus.TCP.Constants;

public static class SunspecConsts
{
    public static ushort C_Manufacturer = 0x9C44;
    public static ushort C_Model = 0x9C54;
    public static ushort C_Version = 0x9C6C;
    public static ushort C_SerialNumber = 0x9C74;
    public static ushort C_DeviceAddress = 0x9C84;

    public static ushort C_SunSpec_DID = 0x9C85;
    public static ushort C_SunSpec_Length = 0x9C86;
    public static ushort I_AC_Current = 0x9C87;
    public static ushort I_AC_CurrentA = 0x9C88;
    public static ushort I_AC_CurrentB = 0x9C89;
    public static ushort I_AC_CurrentC = 0x9C8A;
    public static ushort I_AC_Current_SF = 0x9C8B;
    public static ushort I_AC_VoltageAB = 0x9C8C;
    public static ushort I_AC_VoltageBC = 0x9C8D;
    public static ushort I_AC_VoltageCA = 0x9C8E;
    public static ushort I_AC_VoltageAN = 0x9C8F;
    public static ushort I_AC_VoltageBN = 0x9C90;
    public static ushort I_AC_VoltageCN = 0x9C91;
    public static ushort I_AC_Voltage_SF = 0x9C92;
    public static ushort I_AC_Power = 0x9C93;
    public static ushort I_AC_Power_SF = 0x9C94;
    public static ushort I_AC_Frequency = 0x9C95;
    public static ushort I_AC_Frequency_SF = 0x9C96;
    public static ushort I_AC_VA = 0x9C97;
    public static ushort I_AC_VA_SF = 0x9C98;
    public static ushort I_AC_VAR = 0x9C99;
    public static ushort I_AC_VAR_SF = 0x9C9A;
    public static ushort I_AC_PF = 0x9C9B;
    public static ushort I_AC_PF_SF = 0x9C9C;
    public static ushort I_AC_Energy_WH = 0x9C9D;
    public static ushort I_AC_Energy_WH_SF = 0x9C9F;
    public static ushort I_DC_Current = 0x9CA0;
    public static ushort I_DC_Current_SF = 0x9CA1;
    public static ushort I_DC_Voltage = 0x9CA2;
    public static ushort I_DC_Voltage_SF = 0x9CA3;
    public static ushort I_DC_Power = 0x9CA4;
    public static ushort I_DC_Power_SF = 0x9CA5;
    public static ushort I_Temp_Sink = 0x9CA7;
    public static ushort I_Temp_SF = 0x9CAA;
    public static ushort I_Status = 0x9CAB;
    public static ushort I_Status_Vendor = 0x9CAC;

    public static ushort ActivePowerLimit = 0xF001;

    public static ushort AdvancedPwrControlEn = 0xF142;
    public static ushort CommitPowerControlSettings = 0xF100;
    public static ushort RestorePowerControlDefaultSettings = 0xF101;
    public static ushort ReactivePwrConfig = 0xF104;

    public static ushort EnableDynamicPowerControl = 0xF300;
    public static ushort MaxActivePower = 0xF304;
    public static ushort MaxReactivePower = 0xF306;

    public static ushort Storage_Control_Mode = 0xE004;
    public static ushort Storage_AC_Charge_Policy = 0xE005;
    public static ushort Storage_AC_Charge_Limit = 0xE006;
    public static ushort Storage_Backup_Reserved_Setting = 0xE008;
    public static ushort Storage_Charge_Discharge_Default_Mode = 0xE00A;
    public static ushort Remote_Control_Command_Timeout = 0xE00B;
    public static ushort Remote_Control_Command_Mode = 0xE00D;
    public static ushort Remote_Control_Charge_Limit = 0xE00E;
    public static ushort Remote_Control_Command_Discharge_Limit = 0xE010;

    public static ushort Battery_1_Manufacturer_Name = 0xE100;
    public static ushort Battery_1_Model = 0xE110;
    public static ushort Battery_1_Firmware_Version = 0xE120;
    public static ushort Battery_1_Serial_Number = 0xE130;
    public static ushort Battery_1_Device_ID = 0xE140;
    public static ushort Battery_1_Rated_Energy = 0xE142;
    public static ushort Battery_1_Max_Charge_Continues_Power = 0xE144;
    public static ushort Battery_1_Max_Discharge_Continues_Power = 0xE146;
    public static ushort Battery_1_Max_Charge_Peak_Power = 0xE148;
    public static ushort Battery_1_Max_Discharge_Peak_Power = 0xE14A;
    public static ushort Battery_1_Average_Temperature = 0xE16C;
    public static ushort Battery_1_Max_Temperature = 0xE16E;
    public static ushort Battery_1_Instantaneous_Voltage = 0xE170;
    public static ushort Battery_1_Instantaneous_Current = 0xE172;
    public static ushort Battery_1_Instantaneous_Power = 0xE174;
    public static ushort Battery_1_Lifetime_Export_Energy_Counter = 0xE176;
    public static ushort Battery_1_Lifetime_Import_Energy_Counter = 0xE17A;
    public static ushort Battery_1_Max_Energy = 0xE17E;
    public static ushort Battery_1_Available_Energy = 0xE180;
    public static ushort Battery_1_State_of_Health = 0xE182;
    public static ushort Battery_1_State_of_Energy = 0xE184;
    public static ushort Battery_1_Status = 0xE186;
    public static ushort Battery_1_Status_Internal = 0xE188;

    public static ushort Battery_2_Manufacturer_Name = 0xE200;
    public static ushort Battery_2_Model = 0xE210;
    public static ushort Battery_2_Firmware_Version = 0xE220;
    public static ushort Battery_2_Serial_Number = 0xE230;
    public static ushort Battery_2_Device_ID = 0xE240;
    public static ushort Battery_2_Rated_Energy = 0xE242;
    public static ushort Battery_2_Max_Charge_Continues_Power = 0xE244;
    public static ushort Battery_2_Max_Discharge_Continues_Power = 0xE246;
    public static ushort Battery_2_Max_Charge_Peak_Power = 0xE248;
    public static ushort Battery_2_Max_Discharge_Peak_Power = 0xE24A;
    public static ushort Battery_2_Average_Temperature = 0xE26C;
    public static ushort Battery_2_Max_Temperature = 0xE26E;
    public static ushort Battery_2_Instantaneous_Voltage = 0xE270;
    public static ushort Battery_2_Instantaneous_Current = 0xE272;
    public static ushort Battery_2_Instantaneous_Power = 0xE274;
    public static ushort Battery_2_Lifetime_Export_Energy_Counter = 0xE276;
    public static ushort Battery_2_Lifetime_Import_Energy_Counter = 0xE27A;
    public static ushort Battery_2_Max_Energy = 0xE27E;
    public static ushort Battery_2_Available_Energy = 0xE280;
    public static ushort Battery_2_State_of_Health = 0xE282;
    public static ushort Battery_2_State_of_Energy = 0xE284;
    public static ushort Battery_2_Status = 0xE286;
    public static ushort Battery_2_Status_Internal = 0xE288;




    public static Dictionary<ushort, SunspecDefinition> SunspecDefinitions = new()
    {
        { C_Manufacturer, new SunspecDefinition { Name = nameof(C_Manufacturer), Address = C_Manufacturer, Size = 16, Type = typeof(String32), Description = "Value Registered with SunSpec = \"SolarEdge\"" } },
        { C_Model, new SunspecDefinition { Name = nameof(C_Model), Address = C_Model, Size = 16, Type = typeof(String32), Description = "Is set to the appropriate inverter model, e.g. SE5000" } },
        { C_Version, new SunspecDefinition { Name = nameof(C_Version), Address = C_Version, Size = 8, Type = typeof(String16), Description = "Contains the CPU software version with leading zeroes, e.g. 0002.0611" } },
        { C_SerialNumber, new SunspecDefinition { Name = nameof(C_SerialNumber), Address = C_SerialNumber, Size = 16, Type = typeof(String32), Description = "Contains the inverter serial number" } },
        { C_DeviceAddress, new SunspecDefinition { Name = nameof(C_DeviceAddress), Address = C_DeviceAddress, Size = 1, Type = typeof(Types.UInt16), Description = "Is the device MODBUS ID" } },

        { C_SunSpec_DID, new SunspecDefinition { Name = nameof(C_SunSpec_DID), Address = C_SunSpec_DID, Size = 1, Type = typeof(Types.UInt16), Description = "101 = single phase, 102 = split phase, 103 = three phase" } },
        { C_SunSpec_Length, new SunspecDefinition { Name = nameof(C_SunSpec_Length), Address = C_SunSpec_Length, Size = 1, Type = typeof(Types.UInt16), Units = "Registers", Description = "50 = Length of model block" } },
        { I_AC_Current, new SunspecDefinition { Name = nameof(I_AC_Current), Address = I_AC_Current, Size = 1, Type = typeof(Types.UInt16), Units = "Amps", Description = "AC Total Current value" } },
        { I_AC_CurrentA, new SunspecDefinition { Name = nameof(I_AC_CurrentA), Address = I_AC_CurrentA, Size = 1, Type = typeof(Types.UInt16), Units = "Amps", Description = "AC Phase A Total Current value" } },
        { I_AC_CurrentB, new SunspecDefinition { Name = nameof(I_AC_CurrentB), Address = I_AC_CurrentB, Size = 1, Type = typeof(Types.UInt16), Units = "Amps", Description = "AC Phase B Total Current value" } },
        { I_AC_CurrentC, new SunspecDefinition { Name = nameof(I_AC_CurrentC), Address = I_AC_CurrentC, Size = 1, Type = typeof(Types.UInt16), Units = "Amps", Description = "AC Phase C Total Current value" } },
        { I_AC_Current_SF, new SunspecDefinition { Name = nameof(I_AC_Current_SF), Address = I_AC_Current_SF, Size = 1, Type = typeof(Types.Int16), Description = "AC Current scale factor" } },
        { I_AC_VoltageAB, new SunspecDefinition { Name = nameof(I_AC_VoltageAB), Address = I_AC_VoltageAB, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "AC Voltage Phase AB value" } },
        { I_AC_VoltageBC, new SunspecDefinition { Name = nameof(I_AC_VoltageBC), Address = I_AC_VoltageBC, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "AC Voltage Phase BC value" } },
        { I_AC_VoltageCA, new SunspecDefinition { Name = nameof(I_AC_VoltageCA), Address = I_AC_VoltageCA, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "AC Voltage Phase CA value" } },
        { I_AC_VoltageAN, new SunspecDefinition { Name = nameof(I_AC_VoltageAN), Address = I_AC_VoltageAN, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "AC Voltage Phase A to N value" } },
        { I_AC_VoltageBN, new SunspecDefinition { Name = nameof(I_AC_VoltageBN), Address = I_AC_VoltageBN, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "AC Voltage Phase B to N value" } },
        { I_AC_VoltageCN, new SunspecDefinition { Name = nameof(I_AC_VoltageCN), Address = I_AC_VoltageCN, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "AC Voltage Phase C to N value" } },
        { I_AC_Voltage_SF, new SunspecDefinition { Name = nameof(I_AC_Voltage_SF), Address = I_AC_Voltage_SF, Size = 1, Type = typeof(Types.Int16), Description = "AC Voltage scale factor" } },
        { I_AC_Power, new SunspecDefinition { Name = nameof(I_AC_Power), Address = I_AC_Power, Size = 1, Type = typeof(Types.Int16), Units = "Watts", Description = "AC Power value" } },
        { I_AC_Power_SF, new SunspecDefinition { Name = nameof(I_AC_Power_SF), Address = I_AC_Power_SF, Size = 1, Type = typeof(Types.Int16), Description = "AC Power scale factor" } },
        { I_AC_Frequency, new SunspecDefinition { Name = nameof(I_AC_Frequency), Address = I_AC_Frequency, Size = 1, Type = typeof(Types.UInt16), Units = "Hertz", Description = "AC Frequency value" } },
        { I_AC_Frequency_SF, new SunspecDefinition { Name = nameof(I_AC_Frequency_SF), Address = I_AC_Frequency_SF, Size = 1, Type = typeof(Types.Int16), Description = "AC Frequency scale factor" } },
        { I_AC_VA, new SunspecDefinition { Name = nameof(I_AC_VA), Address = I_AC_VA, Size = 1, Type = typeof(Types.Int16), Units = "VA", Description = "Apparent Power" } },
        { I_AC_VA_SF, new SunspecDefinition { Name = nameof(I_AC_VA_SF), Address = I_AC_VA_SF, Size = 1, Type = typeof(Types.Int16), Description = "Apparent Power scale factor" } },
        { I_AC_VAR, new SunspecDefinition { Name = nameof(I_AC_VAR), Address = I_AC_VAR, Size = 1, Type = typeof(Types.Int16), Units = "VAR", Description = "Reactive Power" } },
        { I_AC_VAR_SF, new SunspecDefinition { Name = nameof(I_AC_VAR_SF), Address = I_AC_VAR_SF, Size = 1, Type = typeof(Types.Int16), Description = "Reactive Power scale factor" } },
        { I_AC_PF, new SunspecDefinition { Name = nameof(I_AC_PF), Address = I_AC_PF, Size = 1, Type = typeof(Types.Int16), Units = "%", Description = "Power Factor" } },
        { I_AC_PF_SF, new SunspecDefinition { Name = nameof(I_AC_PF_SF), Address = I_AC_PF_SF, Size = 1, Type = typeof(Types.Int16), Description = "Power Factor scale factor" } },
        { I_AC_Energy_WH, new SunspecDefinition { Name = nameof(I_AC_Energy_WH), Address = I_AC_Energy_WH, Size = 2, Type = typeof(Acc32), Units = "Wh", Description = "AC Lifetime Energy production" } },
        { I_AC_Energy_WH_SF, new SunspecDefinition { Name = nameof(I_AC_Energy_WH_SF), Address = I_AC_Energy_WH_SF, Size = 1, Type = typeof(Types.Int16), Description = "AC Lifetime Energy production scale factor" } },
        { I_DC_Current, new SunspecDefinition { Name = nameof(I_DC_Current), Address = I_DC_Current, Size = 1, Type = typeof(Types.UInt16), Units = "Amps", Description = "DC Current value" } },
        { I_DC_Current_SF, new SunspecDefinition { Name = nameof(I_DC_Current_SF), Address = I_DC_Current_SF, Size = 1, Type = typeof(Types.Int16), Description = "DC Current scale factor" } },
        { I_DC_Voltage, new SunspecDefinition { Name = nameof(I_DC_Voltage), Address = I_DC_Voltage, Size = 1, Type = typeof(Types.UInt16), Units = "Volts", Description = "DC Voltage value" } },
        { I_DC_Voltage_SF, new SunspecDefinition { Name = nameof(I_DC_Voltage_SF), Address = I_DC_Voltage_SF, Size = 1, Type = typeof(Types.Int16), Description = "DC Voltage scale factor" } },
        { I_DC_Power, new SunspecDefinition { Name = nameof(I_DC_Power), Address = I_DC_Power, Size = 1, Type = typeof(Types.Int16), Units = "Watts", Description = "DC Power value" } },
        { I_DC_Power_SF, new SunspecDefinition { Name = nameof(I_DC_Power_SF), Address = I_DC_Power_SF, Size = 1, Type = typeof(Types.Int16), Description = "DC Power scale factor" } },
        { I_Temp_Sink, new SunspecDefinition { Name = nameof(I_Temp_Sink), Address = I_Temp_Sink, Size = 1, Type = typeof(Types.Int16), Units = "°C", Description = "Heat Sink Temperature" } },
        { I_Temp_SF, new SunspecDefinition { Name = nameof(I_Temp_SF), Address = I_Temp_SF, Size = 1, Type = typeof(Types.Int16), Description = "Heat Sink Temperature scale factor" } },
        { I_Status, new SunspecDefinition { Name = nameof(I_Status), Address = I_Status, Size = 1, Type = typeof(Types.UInt16), Description = "Operating State" } },
        { I_Status_Vendor, new SunspecDefinition { Name = nameof(I_Status_Vendor), Address = I_Status_Vendor, Size = 1, Type = typeof(Types.UInt16), Description = "Vendor-defined operating state and error codes. For error description, meaning and troubleshooting, refer to the SolarEdge Installation Guide" } },

        { ActivePowerLimit, new SunspecDefinition { Name = nameof(ActivePowerLimit), Address = ActivePowerLimit, Size = 1, Type = typeof(Types.UInt16), Description = "Active Power Limit (%)" } },

        { AdvancedPwrControlEn, new SunspecDefinition { Name = nameof(AdvancedPwrControlEn), Address = AdvancedPwrControlEn, Size = 2, Type = typeof(Types.UInt32), Description = "(0..1)" } },
        { CommitPowerControlSettings, new SunspecDefinition { Name = nameof(CommitPowerControlSettings), Address = CommitPowerControlSettings, Size = 1, Type = typeof(Types.UInt16), Description = "Commit power control settings" } },
        { RestorePowerControlDefaultSettings, new SunspecDefinition { Name = nameof(RestorePowerControlDefaultSettings), Address = RestorePowerControlDefaultSettings, Size = 1, Type = typeof(Types.UInt16), Description = "Restore Power Control Default Settings" } },
        { ReactivePwrConfig, new SunspecDefinition { Name = nameof(ReactivePwrConfig), Address = ReactivePwrConfig, Size = 2, Type = typeof(Types.UInt32), Description = "(0..4)" } },

        { EnableDynamicPowerControl, new SunspecDefinition { Name = nameof(EnableDynamicPowerControl), Address = EnableDynamicPowerControl, Size = 1, Type = typeof(Types.UInt16), Description = "Enable dynamic power control" } },
        { MaxActivePower, new SunspecDefinition { Name = nameof(MaxActivePower), Address = MaxActivePower, Size = 2, Type = typeof(Types.Float32), Description = "Max active power" } },
        { MaxReactivePower, new SunspecDefinition { Name = nameof(MaxReactivePower), Address = MaxReactivePower, Size = 2, Type = typeof(Types.Float32), Description = "Max reactive power" } },

        { Storage_Control_Mode, new SunspecDefinition { Name = nameof(Storage_Control_Mode), Address = Storage_Control_Mode, Size = 1, Type = typeof(Types.UInt16), Description = "Storage Control Mode is used to set the StorEdge system operating mode (0..4)" } },
        { Storage_AC_Charge_Policy, new SunspecDefinition { Name = nameof(Storage_AC_Charge_Policy), Address = Storage_AC_Charge_Policy, Size = 1, Type = typeof(Types.UInt16), Description = "Storage AC Charge Policy is used to enable charging for AC and the limit of yearly AC charge (if applicable) (0..3)" } },
        { Storage_AC_Charge_Limit, new SunspecDefinition { Name = nameof(Storage_AC_Charge_Limit), Address = Storage_AC_Charge_Limit, Size = 2, Type = typeof(Types.Float32), Description = "Storage AC Charge Limit is used to set the AC charge limit according to the policy set in the previous register. Either fixed in kWh or percentage is set (e.g. 100KWh or 70%). Relevant only for Storage AC Charge Policy = 2 or 3." } },
        { Storage_Backup_Reserved_Setting, new SunspecDefinition { Name = nameof(Storage_Backup_Reserved_Setting), Address = Storage_Backup_Reserved_Setting, Size = 2, Type = typeof(Types.Float32), Description = "Storage Backup Reserved Setting sets the percentage of reserved battery SOE to be used for backup purposes. Relevant only for inverters with backup functionality." } },
        { Storage_Charge_Discharge_Default_Mode, new SunspecDefinition { Name = nameof(Storage_Charge_Discharge_Default_Mode), Address = Storage_Charge_Discharge_Default_Mode, Size = 1, Type = typeof(Types.UInt16), Description = "Storage Charge/Discharge default Mode sets the default mode of operation when Remote Control Command Timeout has expired. (0..7)" } },
        { Remote_Control_Command_Timeout, new SunspecDefinition { Name = nameof(Remote_Control_Command_Timeout), Address = Remote_Control_Command_Timeout, Size = 2, Type = typeof(Types.UInt32), Description = "Remote Control Command Timeout sets the operating timeframe for the charge/discharge command sets in Remote Control Command Mode register. When expired, it reverts to the default mode defined in Storage Charge/Discharge default Mode register." } },
        { Remote_Control_Command_Mode, new SunspecDefinition { Name = nameof(Remote_Control_Command_Mode), Address = Remote_Control_Command_Mode, Size = 1, Type = typeof(Types.UInt16), Description = "Remote Control Command Mode sets the operating mode during the defined time frame according to the selected Storage Charge/Discharge Mode (see Storage Charge/Discharge default Mode above for the different modes)" } },
        { Remote_Control_Charge_Limit, new SunspecDefinition { Name = nameof(Remote_Control_Charge_Limit), Address = Remote_Control_Charge_Limit, Size = 2, Type = typeof(Types.Float32), Description = "Remote Control Charge Limit sets the maximum charge limit. The default is the maximum battery charge power." } },
        { Remote_Control_Command_Discharge_Limit, new SunspecDefinition { Name = nameof(Remote_Control_Command_Discharge_Limit), Address = Remote_Control_Command_Discharge_Limit, Size = 2, Type = typeof(Types.Float32), Description = "Remote Control Charge Limit sets the maximum discharge limit. The default is the maximum battery discharge power." } },

        { Battery_1_Manufacturer_Name, new SunspecDefinition { Name = nameof(Battery_1_Manufacturer_Name), Address = Battery_1_Manufacturer_Name, Size = 16, Type = typeof(String32), Description = "Battery 1 Manufacturer Name" } },
        { Battery_1_Model, new SunspecDefinition { Name = nameof(Battery_1_Model), Address = Battery_1_Model, Size = 16, Type = typeof(String32), Description = "Battery 1 Model" } },
        { Battery_1_Firmware_Version, new SunspecDefinition { Name = nameof(Battery_1_Firmware_Version), Address = Battery_1_Firmware_Version, Size = 16, Type = typeof(String32), Description = "Battery 1 Firmware Version" } },
        { Battery_1_Serial_Number, new SunspecDefinition { Name = nameof(Battery_1_Serial_Number), Address = Battery_1_Serial_Number, Size = 16, Type = typeof(String32), Description = "Battery 1 Serial Number" } },
        { Battery_1_Device_ID, new SunspecDefinition { Name = nameof(Battery_1_Device_ID), Address = Battery_1_Device_ID, Size = 1, Type = typeof(Types.UInt16), Description = "Battery 1 Device ID" } },
        { Battery_1_Rated_Energy, new SunspecDefinition { Name = nameof(Battery_1_Rated_Energy), Address = Battery_1_Rated_Energy, Size = 2, Type = typeof(Float32), Units = "Wh", Description = "Battery 1 Rated Energy" } },
        { Battery_1_Max_Charge_Continues_Power, new SunspecDefinition { Name = nameof(Battery_1_Max_Charge_Continues_Power), Address = Battery_1_Max_Charge_Continues_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 1 Max Charge Continues Power" } },
        { Battery_1_Max_Discharge_Continues_Power, new SunspecDefinition { Name = nameof(Battery_1_Max_Discharge_Continues_Power), Address = Battery_1_Max_Discharge_Continues_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 1 Max Discharge Continues Power" } },
        { Battery_1_Max_Charge_Peak_Power, new SunspecDefinition { Name = nameof(Battery_1_Max_Charge_Peak_Power), Address = Battery_1_Max_Charge_Peak_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 1 Max Charge Peak Power" } },
        { Battery_1_Max_Discharge_Peak_Power, new SunspecDefinition { Name = nameof(Battery_1_Max_Discharge_Peak_Power), Address = Battery_1_Max_Discharge_Peak_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 1 Max Discharge Peak Power" } },
        { Battery_1_Average_Temperature, new SunspecDefinition { Name = nameof(Battery_1_Average_Temperature), Address = Battery_1_Average_Temperature, Size = 2, Type = typeof(Float32), Units = "°C", Description = "Battery 1 Average Temperature" } },
        { Battery_1_Max_Temperature, new SunspecDefinition { Name = nameof(Battery_1_Max_Temperature), Address = Battery_1_Max_Temperature, Size = 2, Type = typeof(Float32), Units = "°C", Description = "Battery 1 Max Temperature" } },
        { Battery_1_Instantaneous_Voltage, new SunspecDefinition { Name = nameof(Battery_1_Instantaneous_Voltage), Address = Battery_1_Instantaneous_Voltage, Size = 2, Type = typeof(Float32), Units = "Volts", Description = "Battery 1 Instantaneous Voltage" } },
        { Battery_1_Instantaneous_Current, new SunspecDefinition { Name = nameof(Battery_1_Instantaneous_Current), Address = Battery_1_Instantaneous_Current, Size = 2, Type = typeof(Float32), Units = "Amps", Description = "Battery 1 Instantaneous Current" } },
        { Battery_1_Instantaneous_Power, new SunspecDefinition { Name = nameof(Battery_1_Instantaneous_Power), Address = Battery_1_Instantaneous_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 1 Instantaneous Power" } },
        { Battery_1_Lifetime_Export_Energy_Counter, new SunspecDefinition { Name = nameof(Battery_1_Lifetime_Export_Energy_Counter), Address = Battery_1_Lifetime_Export_Energy_Counter, Size = 4, Type = typeof(Types.UInt64), Units = "Wh", Description = "Battery 1 Lifetime Export Energy Counter" } },
        { Battery_1_Lifetime_Import_Energy_Counter, new SunspecDefinition { Name = nameof(Battery_1_Lifetime_Import_Energy_Counter), Address = Battery_1_Lifetime_Import_Energy_Counter, Size = 4, Type = typeof(Types.UInt64), Units = "Wh", Description = "Battery 1 Lifetime Import Energy Counter" } },
        { Battery_1_Max_Energy, new SunspecDefinition { Name = nameof(Battery_1_Max_Energy), Address = Battery_1_Max_Energy, Size = 2, Type = typeof(Float32), Units = "Wh", Description = "Battery 1 Max Energy" } },
        { Battery_1_Available_Energy, new SunspecDefinition { Name = nameof(Battery_1_Available_Energy), Address = Battery_1_Available_Energy, Size = 2, Type = typeof(Float32), Units = "Wh", Description = "Battery 1 Available Energy" } },
        { Battery_1_State_of_Health, new SunspecDefinition { Name = nameof(Battery_1_Instantaneous_Power), Address = Battery_1_State_of_Health, Size = 2, Type = typeof(Float32), Units = "%", Description = "Battery 1 State of Health (SOH)" } },
        { Battery_1_State_of_Energy, new SunspecDefinition { Name = nameof(Battery_1_State_of_Energy), Address = Battery_1_State_of_Energy, Size = 2, Type = typeof(Float32), Units = "%", Description = "Battery 1 State of Energy (SOE)" } },
        { Battery_1_Status, new SunspecDefinition { Name = nameof(Battery_1_Status), Address = Battery_1_Status, Size = 2, Type = typeof(Types.UInt32), Description = "Battery 1 Status" } },
        { Battery_1_Status_Internal, new SunspecDefinition { Name = nameof(Battery_1_Status_Internal), Address = Battery_1_Status_Internal, Size = 2, Type = typeof(Types.UInt32), Description = "Battery 1 Status Internal" } },

        { Battery_2_Manufacturer_Name, new SunspecDefinition { Name = nameof(Battery_2_Manufacturer_Name), Address = Battery_2_Manufacturer_Name, Size = 16, Type = typeof(String32), Description = "Battery 2 Manufacturer Name" } },
        { Battery_2_Model, new SunspecDefinition { Name = nameof(Battery_2_Model), Address = Battery_2_Model, Size = 16, Type = typeof(String32), Description = "Battery 2 Model" } },
        { Battery_2_Firmware_Version, new SunspecDefinition { Name = nameof(Battery_2_Firmware_Version), Address = Battery_2_Firmware_Version, Size = 16, Type = typeof(String32), Description = "Battery 2 Firmware Version" } },
        { Battery_2_Serial_Number, new SunspecDefinition { Name = nameof(Battery_2_Serial_Number), Address = Battery_2_Serial_Number, Size = 16, Type = typeof(String32), Description = "Battery 2 Serial Number" } },
        { Battery_2_Device_ID, new SunspecDefinition { Name = nameof(Battery_2_Device_ID), Address = Battery_2_Device_ID, Size = 1, Type = typeof(Types.UInt16), Description = "Battery 2 Device ID" } },
        { Battery_2_Rated_Energy, new SunspecDefinition { Name = nameof(Battery_2_Rated_Energy), Address = Battery_2_Rated_Energy, Size = 2, Type = typeof(Float32), Units = "Wh", Description = "Battery 2 Rated Energy" } },
        { Battery_2_Max_Charge_Continues_Power, new SunspecDefinition { Name = nameof(Battery_2_Max_Charge_Continues_Power), Address = Battery_2_Max_Charge_Continues_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 2 Max Charge Continues Power" } },
        { Battery_2_Max_Discharge_Continues_Power, new SunspecDefinition { Name = nameof(Battery_2_Max_Discharge_Continues_Power), Address = Battery_2_Max_Discharge_Continues_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 2 Max Discharge Continues Power" } },
        { Battery_2_Max_Charge_Peak_Power, new SunspecDefinition { Name = nameof(Battery_2_Max_Charge_Peak_Power), Address = Battery_2_Max_Charge_Peak_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 2 Max Charge Peak Power" } },
        { Battery_2_Max_Discharge_Peak_Power, new SunspecDefinition { Name = nameof(Battery_2_Max_Discharge_Peak_Power), Address = Battery_2_Max_Discharge_Peak_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 2 Max Discharge Peak Power" } },
        { Battery_2_Average_Temperature, new SunspecDefinition { Name = nameof(Battery_2_Average_Temperature), Address = Battery_2_Average_Temperature, Size = 2, Type = typeof(Float32), Units = "°C", Description = "Battery 2 Average Temperature" } },
        { Battery_2_Max_Temperature, new SunspecDefinition { Name = nameof(Battery_2_Max_Temperature), Address = Battery_2_Max_Temperature, Size = 2, Type = typeof(Float32), Units = "°C", Description = "Battery 2 Max Temperature" } },
        { Battery_2_Instantaneous_Voltage, new SunspecDefinition { Name = nameof(Battery_2_Instantaneous_Voltage), Address = Battery_2_Instantaneous_Voltage, Size = 2, Type = typeof(Float32), Units = "Volts", Description = "Battery 2 Instantaneous Voltage" } },
        { Battery_2_Instantaneous_Current, new SunspecDefinition { Name = nameof(Battery_2_Instantaneous_Current), Address = Battery_2_Instantaneous_Current, Size = 2, Type = typeof(Float32), Units = "Amps", Description = "Battery 2 Instantaneous Current" } },
        { Battery_2_Instantaneous_Power, new SunspecDefinition { Name = nameof(Battery_2_Instantaneous_Power), Address = Battery_2_Instantaneous_Power, Size = 2, Type = typeof(Float32), Units = "Watts", Description = "Battery 2 Instantaneous Power" } },
        { Battery_2_Lifetime_Export_Energy_Counter, new SunspecDefinition { Name = nameof(Battery_2_Lifetime_Export_Energy_Counter), Address = Battery_2_Lifetime_Export_Energy_Counter, Size = 4, Type = typeof(Types.UInt64), Units = "Wh", Description = "Battery 2 Lifetime Export Energy Counter" } },
        { Battery_2_Lifetime_Import_Energy_Counter, new SunspecDefinition { Name = nameof(Battery_2_Lifetime_Import_Energy_Counter), Address = Battery_2_Lifetime_Import_Energy_Counter, Size = 4, Type = typeof(Types.UInt64), Units = "Wh", Description = "Battery 2 Lifetime Import Energy Counter" } },
        { Battery_2_Max_Energy, new SunspecDefinition { Name = nameof(Battery_2_Max_Energy), Address = Battery_2_Max_Energy, Size = 2, Type = typeof(Float32), Units = "Wh", Description = "Battery 2 Max Energy" } },
        { Battery_2_Available_Energy, new SunspecDefinition { Name = nameof(Battery_2_Available_Energy), Address = Battery_2_Available_Energy, Size = 2, Type = typeof(Float32), Units = "Wh", Description = "Battery 2 Available Energy" } },
        { Battery_2_State_of_Health, new SunspecDefinition { Name = nameof(Battery_2_Instantaneous_Power), Address = Battery_2_State_of_Health, Size = 2, Type = typeof(Float32), Units = "%", Description = "Battery 2 State of Health (SOH)" } },
        { Battery_2_State_of_Energy, new SunspecDefinition { Name = nameof(Battery_2_State_of_Energy), Address = Battery_2_State_of_Energy, Size = 2, Type = typeof(Float32), Units = "%", Description = "Battery 2 State of Energy (SOE)" } },
        { Battery_2_Status, new SunspecDefinition { Name = nameof(Battery_2_Status), Address = Battery_2_Status, Size = 2, Type = typeof(Types.UInt32), Description = "Battery 2 Status" } },
        { Battery_2_Status_Internal, new SunspecDefinition { Name = nameof(Battery_2_Status_Internal), Address = Battery_2_Status_Internal, Size = 2, Type = typeof(Types.UInt32), Description = "Battery 2 Status Internal" } },

    };
}
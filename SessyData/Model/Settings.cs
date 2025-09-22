using SessyCommon.Attributes;
using SessyCommon.Extensions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyData.Model
{
    public class Settings : IUpdatable<Settings>
    {
        [Key]
        public int Id { get; set; }

        public bool ManualOverride { get; set; }

        [SkipCopy]
        public string? Hours {  get; set; }

        public string? TimeZone { get; set; }

        public double? CycleCost { get; set; }

        [SkipCopy]
        public string? RequiredHomeEnergy { get; set; }

        public double? NetZeroHomeMinProfit { get; set; }

        public double? SolarCorrection { get; set; }

        public string? DatabaseBackupDirectory { get; set; }

        public bool SolarSystemShutsDownDuringNegativePrices { get; set; }

        [NotMapped]
        public char[]? HoursArray
        {
            get => Hours.StringToArray<char>();
            set => Hours = value.StringFromArray<char>();
        }

        [NotMapped]
        public double[] RequiredHomeEnergyArray
        {
            get => RequiredHomeEnergy.StringToArray<double>();
            set => RequiredHomeEnergy = value.StringFromArray<double>();
        }

        public void Update(Settings updateInfo)
        {
            if (!string.IsNullOrWhiteSpace(updateInfo.Hours) || !string.IsNullOrWhiteSpace(RequiredHomeEnergy))
                throw new InvalidOperationException("Hours and RequiredHomeEnergy should be null or empty, you should fill the arrays instead.");

            this.Copy(updateInfo);

            // Serializing the arrays
            HoursArray = updateInfo.HoursArray;
            RequiredHomeEnergyArray = updateInfo.RequiredHomeEnergyArray;
        }
    }
}

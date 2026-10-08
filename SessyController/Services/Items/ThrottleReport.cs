namespace SessyController.Services.Items
{
    /// <summary>One full-request quarter: SOC at its start, watts asked, AC watts delivered.</summary>
    public sealed record ThrottleSample(double Soc, double RequestedW, double DeliveredW);

    /// <summary>Median delivered power in one SOC band; null where the band has no samples.</summary>
    public sealed record ThrottleBand(
        int SocLowPct, int SocHighPct,
        int ChargeQuarters, double? ChargeMedianW, double? ChargeShare,
        int DischargeQuarters, double? DischargeMedianW, double? DischargeShare)
    {
        public string Label => $"{SocLowPct}-{SocHighPct}%";
    }

    /// <summary>
    /// Totals for one direction. FullCycleHours is a 0-100% pass at the measured band powers,
    /// NameplateCycleHours the same pass at nameplate.
    /// </summary>
    public sealed record ThrottleDirection(
        int Quarters, double RequestedKWh, double DeliveredKWh, double? FullCycleHours, double NameplateCycleHours)
    {
        /// <summary>Delivered over requested energy; 1.0 = no throttle.</summary>
        public double? Share => RequestedKWh > 0.0 ? DeliveredKWh / RequestedKWh : null;

        /// <summary>Part of a full pass done in the nameplate time; 1.0 = no throttle.</summary>
        public double? CycleShare => FullCycleHours is > 0.0 ? NameplateCycleHours / FullCycleHours : null;
    }

    /// <summary>Throttle per SOC band and per direction, for the Statistics page.</summary>
    public sealed record ThrottleReport(
        IReadOnlyList<ThrottleBand> Bands, ThrottleDirection Charge, ThrottleDirection Discharge,
        double CapacityKWh, double ChargeNameplateW, double DischargeNameplateW)
    {
        public static readonly ThrottleReport Empty = new(
            Array.Empty<ThrottleBand>(), new(0, 0.0, 0.0, null, 0.0), new(0, 0.0, 0.0, null, 0.0), 0.0, 0.0, 0.0);

        public bool HasData => Charge.Quarters + Discharge.Quarters > 0;

        /// <summary>
        /// Loss per full cycle (CapacityKWh charged from the grid and discharged). Efficiency is
        /// energy really lost; throttle is energy NOT moved in the time the nameplate needs.
        /// </summary>
        public CycleLoss LossPerCycle(double roundTrip) => new(
            CapacityKWh * (1.0 - roundTrip),
            Charge.CycleShare is double c ? CapacityKWh * (1.0 - c) : null,
            Discharge.CycleShare is double d ? CapacityKWh * (1.0 - d) : null);
    }

    /// <summary>Loss per cycle in kWh; a null throttle part means a band lacks data.</summary>
    public sealed record CycleLoss(double EfficiencyKWh, double? ChargeThrottleKWh, double? DischargeThrottleKWh)
    {
        public double TotalKWh => EfficiencyKWh + (ChargeThrottleKWh ?? 0.0) + (DischargeThrottleKWh ?? 0.0);
    }
}

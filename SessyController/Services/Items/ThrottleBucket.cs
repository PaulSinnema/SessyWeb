namespace SessyController.Services.Items
{
    /// <summary>
    /// Throttle ratio for one temperature bucket. The ratio is realized / requested battery
    /// power, so 1.0 means no throttling and lower values mean the battery delivered less than
    /// planned at that outside temperature. Charge and discharge are tracked separately.
    /// </summary>
    public sealed class ThrottleBucket
    {
        /// <summary>Lower bound of the bucket in °C (e.g. 28 for the 28–30 °C bucket).</summary>
        public int TemperatureLow { get; init; }

        /// <summary>Bucket width in °C.</summary>
        public int Width { get; init; }

        /// <summary>EMA of realized/requested power while discharging. 1.0 = no throttle.</summary>
        public double DischargeRatio { get; set; } = 1.0;

        /// <summary>EMA of realized/requested power while charging. 1.0 = no throttle.</summary>
        public double ChargeRatio { get; set; } = 1.0;

        /// <summary>Number of discharge samples that contributed to DischargeRatio.</summary>
        public int DischargeSamples { get; set; }

        /// <summary>Number of charge samples that contributed to ChargeRatio.</summary>
        public int ChargeSamples { get; set; }
    }
}
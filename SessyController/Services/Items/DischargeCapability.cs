namespace SessyController.Services.Items
{
    /// <summary>
    /// How much discharge power the battery bank can actually deliver at a given state of charge:
    ///
    ///     P(soc) = PlateauW                     when soc >= KneeSoc
    ///            = PlateauW * soc / KneeSoc     when soc <  KneeSoc
    ///
    /// A plateau with a knee, not a slope. Measured on production data the achievable discharge
    /// power is flat from roughly 20% SOC all the way to full (~4100 W of the 5100 W nameplate)
    /// and collapses below it, because a low state of charge means a low cell voltage and the
    /// current limit then buys less power. A straight line through that would understate the
    /// whole flat region.
    ///
    /// Temperature: warmer means less discharge power. An earlier measurement found none
    /// (R2 0.012 on the throttle ratio over all quarters, where the knee dominates), but on
    /// sustained full-request quarters above the knee it is clear: about -45 W per °C
    /// (28-07..08-10, t -7.8), more than SOC explains up there. The plateau is corrected from the
    /// median temperature of those quarters and the knee region scales with it; the temperature is
    /// clamped to the measured range. Prior battery power (preceding 1, 2 and 4 hours, energy
    /// moved in the session, quarters into the session) was tested earlier and was not significant.
    /// </summary>
    public sealed record DischargeCapability(
        double PlateauW,
        double KneeSoc,
        int Samples,
        double TemperatureSlopeWPerC = 0.0,
        double ReferenceTemperatureC = 0.0,
        double MinTemperatureC = 0.0,
        double MaxTemperatureC = 0.0)
    {
        /// <summary>No fit: the caller keeps whatever limit it already had.</summary>
        public static readonly DischargeCapability None = new(0.0, 0.0, 0);

        /// <summary>True when a temperature correction was fitted.</summary>
        public bool HasTemperatureSlope => TemperatureSlopeWPerC != 0.0 && MaxTemperatureC > MinTemperatureC;

        /// <summary>Plateau (W) at this outside temperature, clamped to the measured range.</summary>
        public double PlateauAt(double temperatureC)
        {
            if (!HasTemperatureSlope || double.IsNaN(temperatureC)) return PlateauW;

            double t = Math.Clamp(temperatureC, MinTemperatureC, MaxTemperatureC);
            return Math.Max(0.0, PlateauW + TemperatureSlopeWPerC * (t - ReferenceTemperatureC));
        }

        /// <summary>Deliverable discharge power (W) at this state of charge and outside temperature.</summary>
        public double PowerW(double socFraction, double temperatureC)
        {
            double baseW = PowerW(socFraction);
            if (baseW <= 0.0 || PlateauW <= 0.0) return baseW;

            return baseW * PlateauAt(temperatureC) / PlateauW;
        }

        /// <summary>Deliverable discharge power (W) at this state of charge.</summary>
        public double PowerW(double socFraction)
        {
            if (Samples == 0) return 0.0;

            double f = socFraction < 0.0 ? 0.0 : (socFraction > 1.0 ? 1.0 : socFraction);

            return KneeSoc > 0.0 && f < KneeSoc
                ? PlateauW * f / KneeSoc
                : PlateauW;
        }
    }
}

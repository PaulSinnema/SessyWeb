namespace SessyController.Services.Items
{
    /// <summary>
    /// How much of the nameplate charge power is actually available:
    ///
    ///     ratio = A - B * soc - C * (temp - RefTemperatureC) - D * (t48 - temp)
    ///
    /// Three effects, all fitted on measured samples:
    ///   soc      — CC/CV taper, the dominant term (roughly 1.0 at 20% SOC down to 0.65 at 80%).
    ///   temp     — outside temperature at that moment.
    ///   t48-temp — heat build-up: how much warmer the last 48 hours were than right now. Days of
    ///              sustained heat warm the battery room, which the momentary outside temperature
    ///              does not capture. Measured on production data this term weighs almost twice
    ///              the momentary one (t=-4.4 next to the other two regressors).
    ///
    /// A planner that ignores these plans a charge window that is too short and leaves the battery
    /// short of full.
    /// </summary>
    public sealed record ChargeTaper(double A, double B, double C, double D, int Samples)
    {
        /// <summary>Neutral taper: no fall-off. Used when there are too few samples to fit.</summary>
        public static readonly ChargeTaper None = new(1.0, 0.0, 0.0, 0.0, 0);

        /// <summary>Temperature the fit is centred on, so A keeps its meaning at a normal day.</summary>
        public const double RefTemperatureC = 20.0;

        /// <summary>Never plan below this ratio, however bad the fit.</summary>
        private const double MinRatio = 0.15;

        /// <summary>
        /// Available fraction of nameplate charge power, temperature terms left out. Callers that
        /// have no temperature for a quarter get the fit at the reference temperature.
        /// </summary>
        public double Ratio(double socFraction)
            => Ratio(socFraction, RefTemperatureC, RefTemperatureC);

        /// <summary>
        /// Available fraction of nameplate charge power at this state of charge, outside
        /// temperature and 48-hour mean temperature.
        /// </summary>
        public double Ratio(double socFraction, double temperatureC, double mean48hC)
        {
            double f = socFraction < 0.0 ? 0.0 : (socFraction > 1.0 ? 1.0 : socFraction);

            double r = A
                     - B * f
                     - C * (temperatureC - RefTemperatureC)
                     - D * (mean48hC - temperatureC);

            return r < MinRatio ? MinRatio : (r > 1.0 ? 1.0 : r);
        }
    }
}

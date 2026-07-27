namespace SessyController.Services.Items
{
    /// <summary>
    /// How the maximum charge power falls off as the battery fills (CC/CV taper).
    /// Ratio = A - B * socFraction, fitted by least squares on measured samples.
    ///
    /// The taper is the dominant limit on charge power: measurements show roughly 1.0 at 20% SOC
    /// falling to 0.65 at 80%, while the same samples grouped by outside temperature are flat.
    /// A planner that ignores it plans a charge window that is too short and leaves the battery
    /// short of full.
    /// </summary>
    public sealed record ChargeTaper(double A, double B, int Samples)
    {
        /// <summary>Neutral taper: no fall-off. Used when there are too few samples to fit.</summary>
        public static readonly ChargeTaper None = new(1.0, 0.0, 0);

        /// <summary>Never plan below this ratio, however bad the fit.</summary>
        private const double MinRatio = 0.15;

        /// <summary>Available fraction of nameplate charge power at this state of charge.</summary>
        public double Ratio(double socFraction)
        {
            double f = socFraction < 0.0 ? 0.0 : (socFraction > 1.0 ? 1.0 : socFraction);
            double r = A - B * f;
            return r < MinRatio ? MinRatio : (r > 1.0 ? 1.0 : r);
        }
    }
}

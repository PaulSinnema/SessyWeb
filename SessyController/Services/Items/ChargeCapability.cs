namespace SessyController.Services.Items
{
    /// <summary>
    /// Sustained charge power per state of charge, measured as SOC gain per quarter (DC watts).
    ///
    /// Replaces ChargeTaper and ChargeCapabilityFloor where it has data. Both of those are built
    /// on the power snapshot taken at the end of a quarter and keep the TOP of each SOC bin, so a
    /// single momentary peak sets the bin: on 15-09..06-10 every bin above 40% SOC held a snapshot
    /// at ~100% of the request while the median was 0.5-0.66, and the plan asked 4.7-5.4 kW where
    /// the bank sustained 3.2-3.9 kW. The SOC gain is what a quarter actually stored, so the
    /// median of it is what the plan should expect.
    ///
    /// Only quarters whose previous quarter was charging as well count, so the ramp-up of a charge
    /// block does not drag a bin down. Underestimating is cheap: a quarter planned at its cap
    /// requests nameplate anyway, so the bank still takes what it can.
    /// </summary>
    public sealed record ChargeCapability(double[] BinPowerW, int Samples)
    {
        /// <summary>No measurement: the taper and floor decide.</summary>
        public static readonly ChargeCapability None = new([], 0);

        /// <summary>Sustained DC charge power (W) at this state of charge, or 0 when the bin has no data.</summary>
        public double PowerW(double socFraction)
        {
            if (Samples == 0 || BinPowerW.Length == 0) return 0.0;

            double f = socFraction < 0.0 ? 0.0 : (socFraction > 1.0 ? 1.0 : socFraction);
            int bin = Math.Min((int)(f * BinPowerW.Length), BinPowerW.Length - 1);

            return BinPowerW[bin];
        }

        /// <summary>Bins that carry a measurement.</summary>
        public int CoveredBins => BinPowerW.Count(w => w > 0.0);
    }
}

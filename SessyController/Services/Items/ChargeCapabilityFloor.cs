namespace SessyController.Services.Items
{
    /// <summary>
    /// The charge power the bank has demonstrably accepted, per state of charge — a floor under
    /// <see cref="ChargeTaper"/>, never a replacement for it.
    ///
    /// The taper is fitted on a ratio (realized / requested), so it can only use quarters that
    /// carry a PlannedUnthrottledPowerW: on the production database 223 of 7135 planned quarters,
    /// all from one ten-day heatwave. Fitted on that, it predicted 2.3 kW at 80% SOC while the
    /// measurements show ~5.3 kW across almost the whole range, and the planner believed it — worth
    /// 7.6 kWh of unsold evening energy on 10-08-2026.
    ///
    /// A floor is valid where a fit on watts is not. A high measurement proves the bank CAN take
    /// that power; a low one proves nothing, because the plan may simply have asked for little
    /// (which is why fitting watts against SOC comes out with a positive slope on the charge side —
    /// low-SOC quarters are mostly slow solar charging). A maximum can only be raised by hardware
    /// actually delivering, so it is safe to take the top of each bin and refuse to plan below it.
    ///
    /// It is also what keeps the taper out of its own feedback loop: a suppressed request produces
    /// low measurements, which fit an even lower taper. Over the two-year sample window the good
    /// samples survive, and a percentile of the top of the bin remembers them where a mean forgets.
    ///
    /// Shape differs from DischargeCapability on purpose: no plateau with a knee, but a value per
    /// bin. The charge side has no flat region — the taper falls off across the whole range — so
    /// the floor has to be able to say something different in every bin.
    /// </summary>
    public sealed record ChargeCapabilityFloor(double[] BinFloorsW, int Samples)
    {
        /// <summary>No measurement: no floor, the taper decides alone.</summary>
        public static readonly ChargeCapabilityFloor None = new([], 0);

        /// <summary>Charge power (W) the bank is known to accept at this state of charge, or 0.</summary>
        public double PowerW(double socFraction)
        {
            if (Samples == 0 || BinFloorsW.Length == 0) return 0.0;

            double f = socFraction < 0.0 ? 0.0 : (socFraction > 1.0 ? 1.0 : socFraction);
            int bin = Math.Min((int)(f * BinFloorsW.Length), BinFloorsW.Length - 1);

            return BinFloorsW[bin];
        }

        /// <summary>Bins that carry a floor — how much of the SOC range has been measured.</summary>
        public int CoveredBins => BinFloorsW.Count(w => w > 0.0);
    }
}

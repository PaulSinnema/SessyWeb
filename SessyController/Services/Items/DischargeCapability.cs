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
    /// Deliberately no temperature terms, unlike ChargeTaper. They were measured and are not
    /// there: outside temperature alone explains R2 = 0.012 of the discharge throttle (t = 0.93),
    /// and next to SOC it stays insignificant. Neither is battery power itself a regressor —
    /// prior charge/discharge power over the preceding 1, 2 and 4 hours, energy moved so far in
    /// the session, and quarters into the session were all tested and none reached significance,
    /// with the sign pointing the wrong way for a heat effect. Do not add them back by analogy
    /// with the charge side.
    /// </summary>
    public sealed record DischargeCapability(double PlateauW, double KneeSoc, int Samples)
    {
        /// <summary>No fit: the caller keeps whatever limit it already had.</summary>
        public static readonly DischargeCapability None = new(0.0, 0.0, 0);

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

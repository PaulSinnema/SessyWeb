namespace SessyController.Services.Items
{
    /// <summary>
    /// How one-way efficiency varies with power.
    ///
    /// The planner used to treat efficiency as a constant, which made spreading energy over many
    /// quarters look free. It is not: a large part of the conversion loss is a fixed overhead —
    /// the inverter electronics draw their tens of watts whether they move 500 W or 5000 W — so
    /// the loss as a *fraction* falls as the power rises. Measured on the production database:
    ///
    ///     0-1 kW  0.80      2-3 kW  0.88      4-5 kW  0.92
    ///
    /// Hence the shape: efficiency = Ceiling − Overhead / power. Ceiling is the conversion
    /// efficiency the hardware approaches when the fixed loss stops mattering, Overhead is that
    /// fixed loss expressed in kW.
    ///
    /// Note this is the opposite of the charge taper: the taper limits how much POWER is available
    /// at a given state of charge, this describes how much of the energy survives at a given power.
    /// A battery can be limited to 3 kW and still be at its most efficient there.
    /// </summary>
    public sealed record EfficiencyCurve(
        double ChargeCeiling,
        double ChargeOverheadKW,
        double DischargeCeiling,
        double DischargeOverheadKW,
        int Samples)
    {
        /// <summary>Flat curve — reproduces the old constant-efficiency behaviour exactly.</summary>
        public static EfficiencyCurve Flat(double chargeEfficiency, double dischargeEfficiency)
            => new(chargeEfficiency, 0.0, dischargeEfficiency, 0.0, 0);

        /// <summary>Never return an efficiency outside this range, however odd the fit.</summary>
        private const double MinEfficiency = 0.50;
        private const double MaxEfficiency = 0.99;

        /// <summary>Below this power the curve is not extrapolated any further down.</summary>
        private const double MinPowerKW = 0.25;

        public double ChargeAt(double powerKW)
            => Evaluate(ChargeCeiling, ChargeOverheadKW, powerKW);

        public double DischargeAt(double powerKW)
            => Evaluate(DischargeCeiling, DischargeOverheadKW, powerKW);

        private static double Evaluate(double ceiling, double overheadKW, double powerKW)
        {
            if (overheadKW <= 0.0) return Clamp(ceiling);

            double power = powerKW < MinPowerKW ? MinPowerKW : powerKW;

            return Clamp(ceiling - overheadKW / power);
        }

        private static double Clamp(double efficiency)
            => efficiency < MinEfficiency ? MinEfficiency
             : efficiency > MaxEfficiency ? MaxEfficiency
             : efficiency;
    }
}

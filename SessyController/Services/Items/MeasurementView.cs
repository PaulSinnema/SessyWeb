using SessyCommon.Enums;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services.Items
{
    /// <summary>
    /// A per-quarter view that combines battery telemetry (from QuarterlyMeasurement) with
    /// the grid flows derived from the P1 meter readings (EnergyHistory deltas) and the
    /// prices valid for that quarter. Grid energy is no longer stored on QuarterlyMeasurement;
    /// it is derived here from the cumulative meter readings, which are the single source of
    /// truth for what actually flowed through the meter.
    ///
    /// All energy values are in Wh unless the property name ends in KWh.
    /// </summary>
    public sealed class MeasurementView
    {
        public DateTime Time { get; init; }

        // ── Battery telemetry (QuarterlyMeasurement) ─────────────────────────
        public double BatteryPowerWatts { get; init; }
        public double BatteryStateOfChargeWh { get; init; }
        public Modes BatteryMode { get; init; }
        public bool IsReliable { get; init; }
        public double PlannedRevenueEur { get; init; }

        // ── Grid flows (EnergyHistory delta) ─────────────────────────────────
        /// <summary>Energy imported from grid this quarter in Wh (consumed-tariff delta).</summary>
        public double GridImportWh { get; init; }

        /// <summary>Energy exported to grid this quarter in Wh (produced-tariff delta).</summary>
        public double GridExportWh { get; init; }

        // ── Prices (EPEXPrices + Taxes) ──────────────────────────────────────
        public double BuyingPriceEur { get; init; }
        public double SellingPriceEur { get; init; }

        // ── Derived helpers ──────────────────────────────────────────────────
        public double GridImportKWh => GridImportWh / 1000.0;
        public double GridExportKWh => GridExportWh / 1000.0;

        public double BatteryChargedKWh => BatteryPowerWatts < 0
            ? Math.Abs(BatteryPowerWatts) * 0.25 / 1000.0
            : 0.0;

        public double BatteryDischargedKWh => BatteryPowerWatts > 0
            ? BatteryPowerWatts * 0.25 / 1000.0
            : 0.0;

        /// <summary>
        /// Economic value (EUR) of this quarter's battery discharge. Self-consumed energy
        /// (ZeroNetHome) avoids importing at the buying price, which is worth more than
        /// exporting at the selling price under net metering. Only the part actually
        /// exported to the grid is valued at the selling price.
        /// </summary>
        public double DischargeValueEur
        {
            get
            {
                double discharged = BatteryDischargedKWh;
                if (discharged <= 0.0) return 0.0;

                double exported = Math.Min(GridExportKWh, discharged);
                double selfUsed = Math.Max(discharged - exported, 0.0);
                return selfUsed * BuyingPriceEur + exported * SellingPriceEur;
            }
        }

        /// <summary>
        /// Cost (EUR) of this quarter's battery charge. Only the part drawn from the grid is
        /// paid for; a charge covered by solar surplus never crosses the meter and is free.
        /// Mirror image of <see cref="DischargeValueEur"/> — the two must stay symmetric or
        /// arbitrage figures gain a bias that has nothing to do with the battery.
        /// </summary>
        public double GridChargeCostEur
        {
            get
            {
                double charged = BatteryChargedKWh;
                if (charged <= 0.0) return 0.0;

                double fromGrid = Math.Min(GridImportKWh, charged);
                return fromGrid * BuyingPriceEur;
            }
        }

        /// <summary>Net arbitrage value (EUR): discharge value minus grid-charge cost.</summary>
        public double ArbitrageValueEur => DischargeValueEur - GridChargeCostEur;
    }
}
using SessyCommon.Configurations;
using SessyCommon.Enums;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Managers;
using SessyData.Model;
using static SessyController.Services.Items.ChargingModes;

namespace SessyController.Services.Items
{
    public sealed class QuarterlyInfo
    {
        private const double MinSolarPowerW = 50.0;

        private readonly SettingsService _settingsService;
        private readonly TimeZoneService _timeZoneService;
        private readonly SolarInverterManager _solarInverterManager;

        private QuarterlyInfo(
            DateTime time,
            double marketPrice,
            double buyingPrice,
            double sellingPrice,
            SettingsService settingsService,
            SolarInverterManager solarInverterManager,
            TimeZoneService timeZoneService)
        {
            Time = time;

            _settingsService = settingsService;
            _solarInverterManager = solarInverterManager;
            _timeZoneService = timeZoneService;

            MarketPrice = marketPrice;
            BuyingPrice = buyingPrice;
            SellingPrice = sellingPrice;

            // Default mode is "do nothing / household self-consumption".
            Mode = Modes.ZeroNetHome;
        }

        /// <summary>
        /// Constructor for realized (measured) quarters.
        /// Fills from a QuarterlyMeasurement — IsMeasured is always true.
        /// </summary>
        public QuarterlyInfo(
            QuarterlyMeasurement measurement,
            double solarKWh,
            double planSolarKWh,
            SettingsService settingsService,
            SolarInverterManager solarInverterManager,
            TimeZoneService timeZoneService)
        {
            _settingsService = settingsService;
            _solarInverterManager = solarInverterManager;
            _timeZoneService = timeZoneService;

            Time = measurement.Time;
            BuyingPrice = measurement.BuyingPriceEur;
            SellingPrice = measurement.SellingPriceEur;
            MarketPrice = 0.0;
            SmoothedBuyingPrice = measurement.BuyingPriceEur;
            SmoothedSellingPrice = measurement.SellingPriceEur;
            IsMeasured = true;
            IsPriceExpected = false;

            Mode = measurement.BatteryMode;

            ChargeLeftWh = measurement.BatteryStateOfChargeWh;
            ChargeNeededWh = 0.0;

            // Actual battery power: negative = charging, positive = discharging.
            PlannedChargePowerW = measurement.BatteryPowerWatts < 0 ? Math.Abs(measurement.BatteryPowerWatts) : 0.0;
            PlannedDischargePowerW = measurement.BatteryPowerWatts > 0 ? measurement.BatteryPowerWatts : 0.0;

            // Solar: use inverter measurement if available, fall back to plan solar.
            SolarPowerPerQuarterHour = solarKWh > 0.0 ? solarKWh : planSolarKWh;
            SmoothedSolarPower = SolarPowerPerQuarterHour;
            SolarGlobalRadiation = 0.0;

            EstimatedConsumptionPerQuarterInWatts = 0.0;
            DeltaLowestPrice = 0.0;
        }
        // Factory that does the awaiting (kept, but simplified).
        public static async Task<QuarterlyInfo> CreateAsync(
            DateTime time,
            double marketPrice,
            SettingsService settingsService,
            SolarInverterManager solarInverterManager,
            TimeZoneService timeZoneService,
            CalculationService calculationService)
        {
            double buying = await calculationService.CalculateEnergyPrice(time, true).ConfigureAwait(false) ?? 0.0;
            double selling = await calculationService.CalculateEnergyPrice(time, false).ConfigureAwait(false) ?? 0.0;

            return new QuarterlyInfo(
                time,
                marketPrice,
                buying,
                selling,
                settingsService,
                solarInverterManager,
                timeZoneService);
        }

        /// <summary>
        /// Timestamp (quarter start).
        /// </summary>
        public DateTime Time { get; set; }

        /// <summary>
        /// Raw market price (ENTSO-E), optional for UI/diagnostics.
        /// </summary>
        public double MarketPrice { get; private set; }

        /// <summary>
        /// Your actual buy/sell tariffs (incl. taxes/fees) used for optimization.
        /// </summary>
        public double BuyingPrice { get; private set; }
        public double SellingPrice { get; private set; }

        /// <summary>
        /// Smoothed prices for visualization only.
        /// </summary>
        public double SmoothedBuyingPrice { get; private set; }
        public double SmoothedSellingPrice { get; private set; }

        /// <summary>
        /// Optional diagnostic value; can be used by UI logic that previously depended on it.
        /// </summary>
        public double DeltaLowestPrice { get; private set; }

        /// <summary>
        /// Forecast inputs (filled by services).
        /// </summary>
        public double EstimatedConsumptionPerQuarterInWatts { get; set; }
        public double SolarPowerPerQuarterHour { get; set; } // kWh per quarter-hour
        public double SolarGlobalRadiation { get; set; }
        public double SmoothedSolarPower { get; set; }
        public double SmoothedConsumptionPerQuarterHour { get; set; }

        /// <summary>
        /// Solver plan outputs (optional, but useful for debugging/visualization).
        /// Power is in Watts (W).
        /// </summary>
        public double PlannedChargePowerW { get; private set; }
        public double PlannedDischargePowerW { get; private set; }

        /// <summary>Throttle-free target power (W) the solver would use without throttling.</summary>
        public double PlannedUnthrottledPowerW { get; private set; }

        /// <summary>
        /// Projected FIFO cost basis (EUR/kWh) of the energy stored in the battery at
        /// this quarter, simulated forward through the current plan. Solar-charged energy
        /// is free; grid-charged energy carries its buying price. Used for the cost-basis
        /// chart series and tooltip.
        /// </summary>
        public double ProjectedCostBasisEur { get; private set; }

        public void SetProjectedCostBasis(double eurPerKWh) => ProjectedCostBasisEur = eurPerKWh;

        /// <summary>
        /// Energy state tracking (Wh). Naming kept close to your codebase, but explicit now.
        /// </summary>
        public double ChargeLeftWh { get; private set; }
        public double ChargeNeededWh { get; private set; }

        public double ChargeLeftPercentage(double totalCapacityWh)
            => totalCapacityWh > 0 ? (ChargeLeftWh / totalCapacityWh) * 100.0 : 0.0;

        public double ChargeNeededPercentage(double totalCapacityWh)
            => totalCapacityWh > 0 ? (ChargeNeededWh / totalCapacityWh) * 100.0 : 0.0;

        /// <summary>
        /// True when this quarter has real measured data (QuarterlyMeasurement).
        /// False for planned/forecast quarters.
        /// </summary>
        public bool IsMeasured { get; private set; }

        /// <summary>
        /// True when the price is based on historical averages because the real
        /// day-ahead price is not yet available from the market.
        /// Used for visualization — e.g. to show expected prices in a different color.
        /// </summary>
        public bool IsPriceExpected { get; private set; }

        public void SetPriceExpected(bool isExpected)
        {
            IsPriceExpected = isExpected;
        }

        /// <summary>
        /// Primary mode used by BatteriesService execution.
        /// </summary>
        public Modes Mode { get; private set; }

        public void SetMode(Modes mode)
        {
            Mode = mode;
        }

        public void SetPlanPower(double chargePowerW, double dischargePowerW, double unthrottledPowerW = 0.0)
        {
            PlannedChargePowerW = Math.Max(0, chargePowerW);
            PlannedDischargePowerW = Math.Max(0, dischargePowerW);
            PlannedUnthrottledPowerW = unthrottledPowerW;
        }

        public void SetChargeLeft(double chargeLeftWh) => ChargeLeftWh = chargeLeftWh;
        public void SetChargeNeeded(double chargeNeededWh) => ChargeNeededWh = chargeNeededWh;

        public void SetDeltaLowestPrice(double priceOfEnergyInBatteries)
        {
            // Legacy field; still useful for visualizing deltas.
            // Use the effective price for the chosen mode.
            DeltaLowestPrice = Price - priceOfEnergyInBatteries;
        }

        /// <summary>
        /// Derived helpers for UI/backwards compatibility.
        /// </summary>
        public bool Charging => Mode == Modes.Charging;
        public bool Discharging => Mode == Modes.Discharging;

        // NOTE: In the old model Disabled depended on DeltaLowestPrice and solar vs consumption.
        // Keep the same idea, but make it purely derived so your UI doesn't break.
        public bool Disabled =>
            DeltaLowestPrice < _settingsService.CycleCost &&
            SolarPowerPerQuarterInWatts < EstimatedConsumptionPerQuarterInWatts;

        public bool ZeroNetHome => Mode == Modes.ZeroNetHome || (!(Charging || Discharging || Disabled));

        public double SolarPowerPerQuarterInWatts => SolarPowerPerQuarterHour * 1000.0;

        /// <summary>
        /// Net household load in Wh for this quarter-hour.
        /// Positive = household needs more than solar produces (grid import or battery discharge needed).
        /// Negative = solar surplus (battery can charge or export to grid).
        /// EstimatedConsumptionPerQuarterInWatts is in Watts (average power) → × 0.25h = Wh per quarter.
        /// SolarPowerPerQuarterHour is already in kWh → × 1000 = Wh per quarter.
        /// </summary>
        public double NetLoadWh =>
            EstimatedConsumptionPerQuarterInWatts * 0.25 - SolarPowerPerQuarterHour * 1000.0;

        public bool SellingPriceIsNegative => SellingPrice < 0.0;
        public bool BuyingPriceIsPositive => BuyingPrice >= 0.0;
        public bool SellingPriceIsPositive => SellingPrice >= 0.0;

        /// <summary>
        /// Effective price depending on Mode (legacy behavior).
        /// </summary>
        public double Price
            => Mode == Modes.Charging ? BuyingPrice : SellingPrice;

        public double SmoothedPrice
            => Mode == Modes.Charging ? SmoothedBuyingPrice : SmoothedSellingPrice;

        /// <summary>
        /// Cash-flow estimate for one quarter, for visualization only (battery-only).
        /// This is not used by the MILP; the MILP objective is computed directly.
        ///
        /// Both sides are split by where the energy actually goes, mirroring
        /// MeasurementView.DischargeValueEur / GridChargeCostEur:
        ///  - charging: only the grid-fed part costs money; solar surplus flowing into the
        ///    battery never crosses the meter and is free.
        ///  - discharging: the part covering the household deficit avoids an import at the
        ///    buying price; only the remainder is exported at the selling price.
        /// Valuing everything at a single price produced bogus figures in both directions —
        /// large negative "revenue" for quarters storing their own solar, and understated
        /// revenue for quarters merely covering the house.
        /// </summary>
        public double Profit
        {
            get
            {
                // Use planned powers as the basis for a simple per-quarter profit estimate.
                // Quarter energy = W * 0.25h -> Wh, /1000 -> kWh
                double chargeKWh = (PlannedChargePowerW * 0.25) / 1000.0;
                double dischargeKWh = (PlannedDischargePowerW * 0.25) / 1000.0;

                // NetLoadWh < 0 means solar surplus, > 0 means a household deficit.
                double solarSurplusKWh = Math.Max(0.0, -NetLoadWh) / 1000.0;
                double deficitKWh = Math.Max(0.0, NetLoadWh) / 1000.0;

                double gridChargeKWh = Math.Max(0.0, chargeKWh - solarSurplusKWh);

                double selfUsedKWh = Math.Min(dischargeKWh, deficitKWh);
                double exportedKWh = dischargeKWh - selfUsedKWh;

                return selfUsedKWh * BuyingPrice
                     + exportedKWh * SellingPrice
                     - gridChargeKWh * BuyingPrice;
            }
        }

        /// <summary>
        /// Applies a centered moving average to SmoothedConsumptionPerQuarterHour.
        /// Only applied to non-measured (planned) quarters.
        /// </summary>
        public static void ApplyConsumptionSmoothing(List<QuarterlyInfo> infos, int windowSize = 4)
        {
            int half = windowSize / 2;
            for (int i = 0; i < infos.Count; i++)
            {
                if (infos[i].IsMeasured) continue;

                var range = infos
                    .Skip(Math.Max(0, i - half))
                    .Take(windowSize)
                    .Select(v => v.EstimatedConsumptionPerQuarterInWatts)
                    .ToList();

                infos[i].SmoothedConsumptionPerQuarterHour = range.Any() ? range.Average() : 0.0;
            }
        }

        /// <summary>
        /// Applies a centered moving average to SmoothedSolarPower,
        /// skipping zero values so nearby real measurements fill the gaps.
        /// Only applied to non-measured (planned) quarters.
        /// </summary>
        public static void ApplySolarSmoothing(List<QuarterlyInfo> infos, int windowSize = 4)
        {
            int half = windowSize / 2;
            for (int i = 0; i < infos.Count; i++)
            {
                if (infos[i].IsMeasured) continue;

                var range = infos
                    .Skip(Math.Max(0, i - half))
                    .Take(windowSize)
                    .Select(v => v.SolarPowerPerQuarterHour)
                    .Where(v => v > 0.0)
                    .ToList();

                if (range.Any())
                    infos[i].SmoothedSolarPower = range.Average();
            }
        }

        /// <summary>
        /// Adds a smoothed price to each QuarterlyInfo object in the list.
        /// </summary>
        public static void AddSmoothedPrices(List<QuarterlyInfo> infos, int windowSize = 4)
        {
            if (infos == null || infos.Count == 0) return;

            for (int i = 0; i < infos.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(infos.Count - 1, i + windowSize / 2);

                var range = infos.Skip(start).Take(end - start + 1);

                infos[i].SmoothedBuyingPrice = range.Any() ? range.Average(h => h.BuyingPrice) : 0.0;
                infos[i].SmoothedSellingPrice = range.Any() ? range.Average(h => h.SellingPrice) : 0.0;
            }
        }

        /// <summary>
        /// Helper for visualization.
        /// </summary>
        public double VisualizeInChart()
        {
            if (Charging) return -0.1;
            if (Discharging) return 0.1;
            if (ZeroNetHome) return 0.03;
            if (Disabled) return 0.0;

            return 1.0;
        }

        public string GetDisplayMode()
        {
            return ChargingModes.GetDisplayMode(Mode);
        }

        // Kept in case you still want to use solar activity checks in the UI/debugging.
        private async Task<bool> SolarPowerIsActive()
        {
            var now = _timeZoneService.Now.DateFloorQuarter();

            if (Time == now)
            {
                var totalSolarPowerW = await _solarInverterManager.GetTotalACPowerInWatts().ConfigureAwait(false);
                return totalSolarPowerW > MinSolarPowerW;
            }

            return false;
        }

        public override string ToString()
        {
            return $"{Time}: Mode={Mode}, Buy={BuyingPrice}, Sell={SellingPrice}, IsPriceExpected={IsPriceExpected}, " +
                   $"PlanC(W)={PlannedChargePowerW}, PlanD(W)={PlannedDischargePowerW}, " +
                   $"ChargeLeftWh={ChargeLeftWh}, ChargeNeededWh={ChargeNeededWh}, " +
                   $"Solar(kWh/q)={SolarPowerPerQuarterHour}, Cons(W/q)={EstimatedConsumptionPerQuarterInWatts}, Profit={Profit}";
        }

        /// <summary>
        /// Resets dynamic fields (useful if you reuse objects).
        /// </summary>
        public void Reset()
        {
            Mode = Modes.ZeroNetHome;

            PlannedChargePowerW = 0.0;
            PlannedDischargePowerW = 0.0;
            PlannedUnthrottledPowerW = 0.0;

            ChargeLeftWh = 0.0;
            ChargeNeededWh = 0.0;

            EstimatedConsumptionPerQuarterInWatts = 0.0;

            SolarPowerPerQuarterHour = 0.0;
            SolarGlobalRadiation = 0.0;
            SmoothedSolarPower = 0.0;

            DeltaLowestPrice = 0.0;
            SmoothedBuyingPrice = 0.0;
            SmoothedSellingPrice = 0.0;

            IsPriceExpected = false;
        }
    }
}
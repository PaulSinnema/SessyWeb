using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// This service represents a virtual battery. It calculates the total cost of the 
    /// load in the battery over a Session (charging/discharging).
    /// It uses measured data from the past and falls back to estimates stored
    /// in QuarterlyInfo for missing data and for future quarters.
    /// </summary>
    public class VirtualBatteryService
    {
        private EnergyHistoryDataService _energyHistoryDataService { get; }
        private ConsumptionDataService _consumptionDataService { get; }
        private SolarInverterDataService _solarInverterDataService { get; }

        public VirtualBatteryService(
            EnergyHistoryDataService energyHistoryDataService,
            ConsumptionDataService consumptionDataService,
            SolarInverterDataService solarInverterDataService)
        {
            _energyHistoryDataService = energyHistoryDataService;
            _consumptionDataService = consumptionDataService;
            _solarInverterDataService = solarInverterDataService;
        }

        /// <summary>
        /// Calculates the cost of the battery load for the given session.
        /// 
        /// For each quarter:
        ///   batteryWh = consumptionWh - solarWh - gridWh
        ///   cost      = batteryWh (kWh) * QuarterlyInfo.Price
        /// 
        /// For past quarters (where measurement data exists), measured values are used
        /// and missing values are filled using QuarterlyInfo estimates.
        /// For future quarters (beyond the last known measurement), the result is 
        /// fully based on QuarterlyInfo estimates.
        /// </summary>
        public async Task<double> CalculateLoadCostForSession(Session session)
        {
            var quarterlies = session
                .GetQuarterlyInfoList()
                .OrderBy(q => q.Time)
                .ToList();

            if (!quarterlies.Any())
                return 0.0;

            // We need the previous quarter for grid calculation.
            var sessionStart = quarterlies.First().Time.AddMinutes(-15);
            var sessionEnd = quarterlies.Last().Time.AddMinutes(15);

            var history = await GetEnergyHistoriesData(sessionStart, sessionEnd);
            var consumption = await GetConsumptionData(sessionStart, sessionEnd);
            var solar = await GetSolarData(sessionStart, sessionEnd);

            history = history.OrderBy(h => h.Time).ToList();
            consumption = consumption.OrderBy(c => c.Time).ToList();
            solar = solar.OrderBy(s => s.Time).ToList();

            // Determine the last timestamp for which we have any real data.
            var latestHistoryTime = history.LastOrDefault()?.Time;
            var latestConsumptionTime = consumption.LastOrDefault()?.Time;
            var latestSolarTime = solar.LastOrDefault()?.Time;

            DateTime? latestDataTime = null;

            if (latestHistoryTime.HasValue)
                latestDataTime = latestHistoryTime;

            if (latestConsumptionTime.HasValue)
                latestDataTime = !latestDataTime.HasValue
                    ? latestConsumptionTime
                    : (latestConsumptionTime > latestDataTime ? latestConsumptionTime : latestDataTime);

            if (latestSolarTime.HasValue)
                latestDataTime = !latestDataTime.HasValue
                    ? latestSolarTime
                    : (latestSolarTime > latestDataTime ? latestSolarTime : latestDataTime);

            var totalCost = 0.0;

            foreach (var qi in quarterlies)
            {
                var start = qi.Time;
                var end = start.AddMinutes(15);

                // Grid Wh (import positive, export negative)
                var gridWh = GetNetGridEnergyWhForQuarter(start, end, history, qi, latestDataTime);

                // Consumption Wh
                var consumptionWh = GetConsumptionWhForQuarter(qi, consumption, latestDataTime);

                // Solar Wh
                var solarWh = GetSolarEnergyWhForQuarter(qi, solar, latestDataTime);

                // Positive batteryWh means energy drawn from the battery.
                var batteryWh = consumptionWh - solarWh - gridWh;
                var batteryKWh = batteryWh / 1000.0;

                // QuarterlyInfo.Price uses BuyingPrice when Charging and SellingPrice otherwise.
                var price = qi.Price;

                totalCost += batteryKWh * price;
            }

            return totalCost;
        }

        #region Per-quarter helpers

        /// <summary>
        /// Returns net grid energy (Wh) for the given quarter.
        /// Uses EnergyHistory if possible (for past quarters),
        /// otherwise falls back to an estimator based on QuarterlyInfo.
        /// </summary>
        private double GetNetGridEnergyWhForQuarter(
            DateTime start,
            DateTime end,
            IReadOnlyList<EnergyHistory> history,
            QuarterlyInfo quarterlyInfo,
            DateTime? latestDataTime)
        {
            // If there is no measurement data at all, or this quarter is in the future
            // beyond the last known measurement, we use only estimates.
            var isFutureQuarter =
                !latestDataTime.HasValue || quarterlyInfo.Time > latestDataTime.Value;

            if (!isFutureQuarter)
            {
                var previous = FindLastHistoryBeforeOrAt(start, history);
                var current = FindLastHistoryBeforeOrAt(end, history);

                if (previous != null && current != null && current.Time > previous.Time)
                {
                    return CalculateHistoryEnergyDeltaWh(current, previous);
                }
            }

            // No reliable history data OR future quarter → estimate using QuarterlyInfo.
            return EstimateNetGridEnergyWh(start, end, quarterlyInfo, history);
        }

        /// <summary>
        /// Returns consumption for the quarter (Wh).
        /// For past quarters, prefers measured Consumption data and falls back
        /// to QuarterlyInfo.EstimatedConsumptionPerQuarterHour.
        /// For future quarters, uses QuarterlyInfo only.
        /// </summary>
        private double GetConsumptionWhForQuarter(
            QuarterlyInfo quarterlyInfo,
            IReadOnlyList<Consumption> consumptions,
            DateTime? latestDataTime)
        {
            var start = quarterlyInfo.Time;
            var end = start.AddMinutes(15);

            var isFutureQuarter =
                !latestDataTime.HasValue || quarterlyInfo.Time > latestDataTime.Value;

            if (!isFutureQuarter)
            {
                var item = consumptions
                    .Where(c => c.Time >= start && c.Time < end)
                    .OrderBy(c => c.Time)
                    .FirstOrDefault();

                if (item != null)
                {
                    // NOTE:
                    // In your original code you did:
                    //   totalConsumption += currentConsumption.ConsumptionWh * 0.25;
                    // so we keep the same scaling assumption here.
                    return item.ConsumptionWh * 0.25;
                }
            }

            // No measured consumption or future quarter: use the estimate stored in QuarterlyInfo.
            return EstimateConsumptionWhFromQuarterlyInfo(quarterlyInfo);
        }

        /// <summary>
        /// Returns solar energy for the quarter (Wh).
        /// For past quarters, prefers measured SolarInverterData and falls back
        /// to QuarterlyInfo.SolarPowerPerQuarterHour.
        /// For future quarters, uses QuarterlyInfo only.
        /// </summary>
        private double GetSolarEnergyWhForQuarter(
            QuarterlyInfo quarterlyInfo,
            IReadOnlyList<SolarInverterData> solar,
            DateTime? latestDataTime)
        {
            var start = quarterlyInfo.Time;
            var end = start.AddMinutes(15);

            var isFutureQuarter =
                !latestDataTime.HasValue || quarterlyInfo.Time > latestDataTime.Value;

            if (!isFutureQuarter)
            {
                var item = solar
                    .Where(s => s.Time >= start && s.Time < end)
                    .OrderBy(s => s.Time)
                    .FirstOrDefault();

                if (item != null)
                {
                    // Power (W) * 0.25h = Wh in that quarter.
                    return item.Power * 0.25;
                }
            }

            // No measured solar data or future quarter: use the estimate stored in QuarterlyInfo.
            return EstimateSolarEnergyWhFromQuarterlyInfo(quarterlyInfo);
        }

        #endregion

        #region Estimation based on QuarterlyInfo

        /// <summary>
        /// Estimate consumption (Wh) for a quarter based on QuarterlyInfo.
        /// Assumes EstimatedConsumptionPerQuarterHour is in kWh for this quarter.
        /// </summary>
        private double EstimateConsumptionWhFromQuarterlyInfo(QuarterlyInfo quarterlyInfo)
        {
            if (quarterlyInfo.EstimatedConsumptionPerQuarterHour > 0.0)
                return quarterlyInfo.EstimatedConsumptionPerQuarterHour;

            return 0.0;
        }

        /// <summary>
        /// Estimate solar energy (Wh) for a quarter based on QuarterlyInfo.
        /// Assumes SolarPowerPerQuarterHour is in kWh for this quarter.
        /// </summary>
        private double EstimateSolarEnergyWhFromQuarterlyInfo(QuarterlyInfo quarterlyInfo)
        {
            if (quarterlyInfo.SolarPowerPerQuarterHour > 0.0)
                return quarterlyInfo.SolarPowerPerQuarterHour * 1000;

            return 0.0;
        }

        /// <summary>
        /// Estimate net grid energy (Wh) for a quarter when EnergyHistory data is missing
        /// or when the quarter is in the future.
        /// 
        /// Uses QuarterlyInfo estimates for consumption and solar production, and the
        /// planned battery charge/discharge power.
        /// 
        /// Formula:
        ///   NetGridWh ≈ ConsumptionWh + ChargeWh - SolarWh - DischargeWh
        /// 
        /// Positive result = import from grid; negative = export to grid.
        /// </summary>
        private double EstimateNetGridEnergyWh(
            DateTime start,
            DateTime end,
            QuarterlyInfo quarterlyInfo,
            IReadOnlyList<EnergyHistory> allHistory)
        {
            // 1) Estimated consumption and solar energy in Wh for this quarter.
            var estimatedConsumptionWh = EstimateConsumptionWhFromQuarterlyInfo(quarterlyInfo);
            var estimatedSolarWh = EstimateSolarEnergyWhFromQuarterlyInfo(quarterlyInfo);

            // 2) Planned charge/discharge in Wh over 15 minutes.
            //    15 minutes = 0.25 hour, so Wh = W * 0.25.
            var chargeWh = quarterlyInfo.ToChargeInWatts / 4.0;       // W * 0.25h
            var dischargeWh = quarterlyInfo.ToDischargeInWatts / 4.0; // W * 0.25h

            // 3) Energy balance:
            //    NetGridWh = ConsumptionWh + ChargeWh - SolarWh - DischargeWh
            var netGridWh = estimatedConsumptionWh + chargeWh - estimatedSolarWh - dischargeWh;

            return netGridWh;
        }

        #endregion

        #region Low-level helpers

        /// <summary>
        /// Returns the last EnergyHistory item with Time <= target, or null if none.
        /// </summary>
        private static EnergyHistory? FindLastHistoryBeforeOrAt(
            DateTime target,
            IReadOnlyList<EnergyHistory> history)
        {
            // NOTE: history is assumed to be sorted ascending.
            EnergyHistory? result = null;

            foreach (var h in history)
            {
                if (h.Time <= target)
                {
                    result = h;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Computes net energy delta (Wh) between two EnergyHistory snapshots.
        /// Positive result means net consumption from the grid, negative means net production.
        /// </summary>
        private static double CalculateHistoryEnergyDeltaWh(
            EnergyHistory currentHistory,
            EnergyHistory previousHistory)
        {
            var consumed =
                (currentHistory.ConsumedTariff1 - previousHistory.ConsumedTariff1) +
                (currentHistory.ConsumedTariff2 - previousHistory.ConsumedTariff2);

            var produced =
                (currentHistory.ProducedTariff1 - previousHistory.ProducedTariff1) +
                (currentHistory.ProducedTariff2 - previousHistory.ProducedTariff2);

            var result = consumed - produced;
            return result;
        }

        #endregion

        #region Data access

        private async Task<List<Consumption>> GetConsumptionData(DateTime start, DateTime end)
        {
            return await _consumptionDataService.GetList(async set =>
            {
                var result = set
                    .Where(c => c.Time >= start && c.Time < end)
                    .OrderBy(c => c.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }

        private async Task<List<EnergyHistory>> GetEnergyHistoriesData(DateTime start, DateTime end)
        {
            return await _energyHistoryDataService.GetList(async set =>
            {
                var result = set
                    .Where(eh => eh.Time >= start && eh.Time < end)
                    .OrderBy(eh => eh.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }

        private async Task<List<SolarInverterData>> GetSolarData(DateTime start, DateTime end)
        {
            return await _solarInverterDataService.GetList(async set =>
            {
                var result = set
                    .Where(eh => eh.Time >= start && eh.Time < end)
                    .OrderBy(eh => eh.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }

        #endregion
    }
}

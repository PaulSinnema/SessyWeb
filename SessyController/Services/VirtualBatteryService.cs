using SessyCommon.Extensions;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// This service represents a virtual battery. It calculates the total cost of the 
    /// current load in the battery. 
    /// </summary>
    public class VirtualBatteryService
    {
        private EnergyHistoryDataService _energyHistoryDataService { get; set; }

        private ConsumptionDataService _consumptionDataService { get; set; }

        private SolarInverterDataService _solarInverterDataService { get; set; }

        public VirtualBatteryService(EnergyHistoryDataService energyHistoryDataService,
                                     ConsumptionDataService consumptionDataService,
                                     SolarInverterDataService solarInverterDataService)
        {
            _energyHistoryDataService = energyHistoryDataService;
            _consumptionDataService = consumptionDataService;
            _solarInverterDataService = solarInverterDataService;
        }

        public async Task<double> CalculateLoadCostForPeriod(Session session)
        {
            var totalCost = 0.0;

            foreach (var quarterlyInfo in session.GetQuarterlyInfoList())
            {
                var totalNet = 0.0;
                var totalConsumption = 0.0;
                var totalSolarPower = 0.0;

                var start = quarterlyInfo.Time;
                var end = quarterlyInfo.Time.AddMinutes(15);

                var history = await GetEnergyHistoriesData(start.AddMinutes(-15), end);

                EnergyHistory? previousHistory = null;
                var consumptiion = await GetConsumptionData(start, end);
                var solar = await GetSolarData(start, end);

                var historyEnumerator = history.GetEnumerator();
                var consumptionEnumerator = consumptiion.GetEnumerator();
                var solarEnumerator = solar.GetEnumerator();

                var hasHistoryData = historyEnumerator.MoveNext();
                var hasConsumptionData = consumptionEnumerator.MoveNext();
                var hasSolarInverterData = solarEnumerator.MoveNext();

                while (hasHistoryData && hasConsumptionData)
                {
                    var currentHistory = historyEnumerator.Current;
                    var currentConsumption = consumptionEnumerator.Current;
                    var currentSolar = solarEnumerator.Current;

                    if (currentHistory.Time == currentConsumption.Time)
                    {
                        totalNet += CalculateHisoryPower(currentHistory, previousHistory);
                        totalConsumption += currentConsumption.ConsumptionWh * 0.25; // Per quarter hour

                        if (hasSolarInverterData && currentSolar.Time == currentConsumption.Time)
                        {
                            totalSolarPower += currentSolar.Power * 0.25; // Per quarter hour
                            hasSolarInverterData = solarEnumerator.MoveNext();
                        }

                        var totalBattery = totalConsumption - totalNet - totalSolarPower;

                        totalCost += totalBattery * quarterlyInfo.Price / 1000;

                        hasHistoryData = historyEnumerator.MoveNext();
                        hasConsumptionData = consumptionEnumerator.MoveNext();
                    }
                    else
                    {
                        // Data missing, advance the smaller list
                        if (currentHistory.Time > currentConsumption.Time)
                        {
                            hasConsumptionData = consumptionEnumerator.MoveNext();
                        }
                        else
                        {
                            hasHistoryData = historyEnumerator.MoveNext();

                        }
                    }

                    previousHistory = currentHistory;
                }
            }

            return totalCost;
        }

        private double CalculateHisoryPower(EnergyHistory currentHistory, EnergyHistory? previousHistory)
        {
            var consumed = currentHistory.ConsumedTariff1 - previousHistory.ConsumedTariff1;
            consumed += currentHistory.ConsumedTariff2 - previousHistory.ConsumedTariff2;
            var produced = currentHistory.ProducedTariff1 - previousHistory.ProducedTariff1;
            produced += currentHistory.ProducedTariff2 - previousHistory.ProducedTariff2;

            var result =  consumed - produced;

            return result;
        }

        private async Task<List<Consumption>> GetConsumptionData(DateTime start, DateTime end)
        {
            return await _consumptionDataService.GetList(async (set) =>
            {
                var result = set.Where(c => c.Time >= start&& c.Time < end)
                                .OrderBy(c => c.Time)
                                .ToList();

                return await Task.FromResult(result);
            });
        }

        private async Task<List<EnergyHistory>> GetEnergyHistoriesData(DateTime start, DateTime end)
        {
            return await _energyHistoryDataService.GetList(async (set) =>
            {
                var result = set.Where(eh => eh.Time >= start && eh.Time < end)
                    .OrderBy(eh => eh.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }

        private async Task<List<SolarInverterData>> GetSolarData(DateTime start, DateTime end)
        {
            return await _solarInverterDataService.GetList(async (set) =>
            {
                var result = set.Where(eh => eh.Time >= start && eh.Time < end)
                    .OrderBy(eh => eh.Time)
                    .ToList();

                return await Task.FromResult(result);
            });
        }
    }
}

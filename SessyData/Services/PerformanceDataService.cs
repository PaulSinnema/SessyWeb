using Microsoft.Extensions.DependencyInjection;
using SessyData.Model;

namespace SessyData.Services
{
    public class PerformanceDataService : ServiceBase<Performance>
    {
        public PerformanceDataService(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory) { }

        public async Task<double> CalculateAveragePriceOfChargeInBatteries(double chargingCapacity, double dischargingCapacity, DateTime from, DateTime to)
        {
            var totalCharge = 0.0;
            var totalCost = 0.0;

            var performanceList = await GetList(async (set) =>
            {
                var result = set.Where(p => p.Time >= from && p.Time <= to).ToList();

                return await Task.FromResult(result);
            });


            foreach (var performance in performanceList)
            {
                if (performance.Charging)
                {
                    if (performance.ChargeLeft < performance.ChargeNeeded)
                    {
                        var room = (performance.ChargeNeeded - performance.ChargeLeft);
                        var charge = Math.Min(chargingCapacity, room);

                        charge /= 1000;

                        totalCharge += charge;
                        totalCost += charge * performance.Price;
                    }

                    break;
                }

                if (performance.Discharging)
                {
                    if (performance.ChargeLeft > performance.ChargeNeeded)
                    {
                        var room = (performance.ChargeLeft - performance.ChargeNeeded);
                        var charge = Math.Min(dischargingCapacity, room);

                        charge /= 1000;

                        totalCharge -= charge;
                        totalCost -= charge * performance.Price;
                    }

                    break;
                }

                if (performance.ZeroNetHome)
                {
                    if (performance.ChargeLeft > 0.0)
                    {
                        var room = performance.ChargeLeft;
                        var charge = Math.Min(performance.EstimatedConsumptionPerQuarterHour, room);

                        charge /= 1000;

                        totalCharge -= charge;
                        totalCost -= charge * performance.Price;
                    }

                    break;
                }
            }

            double average = 0.0;

            if (totalCharge > 0.0)
                average = totalCost / totalCharge;

            return average;
        }
    }
}

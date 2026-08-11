using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;

namespace SessyController.Services.Items
{
    /// <summary>
    /// Turns per-second power samples into one InverterMeasurement per completed quarter.
    ///
    /// Extracted from SunspecInverterService so a non-Modbus source can write the same table.
    /// That table is not a nice-to-have: SolarPowerPage, the statistics and
    /// SolarService.CalculateHistoricalPerformanceFactorAsync all read it rather than the live
    /// value, so an inverter service that does not write it produces a working number and an empty
    /// history.
    ///
    /// Plain class, constructed by each inverter service — no DI registration, so it can be tested
    /// with a data service and a clock and nothing else.
    /// </summary>
    public class InverterMeasurementWriter
    {
        private readonly InverterMeasurementDataService _inverterMeasurementService;
        private readonly TimeZoneService _timeZoneService;

        public InverterMeasurementWriter(InverterMeasurementDataService inverterMeasurementService,
                                         TimeZoneService timeZoneService)
        {
            _inverterMeasurementService = inverterMeasurementService;
            _timeZoneService = timeZoneService;
        }

        /// <summary>
        /// Stores every completed quarter present in <paramref name="collectedPowerData"/> and
        /// removes what it stored. Samples of the running quarter are left in place.
        /// </summary>
        public async Task StoreAsync(string providerName,
                                     Dictionary<string, Dictionary<DateTime, double>> collectedPowerData)
        {
            var now = _timeZoneService.Now;

            foreach (var collectionKeyValue in collectedPowerData)
            {
                var id = collectionKeyValue.Key;
                var collection = collectionKeyValue.Value;

                // Group all samples by their quarter-hour timestamp
                var quarterGroups = collection
                    .GroupBy(c => c.Key.DateFloorQuarter())
                    .OrderBy(g => g.Key)
                    .ToList();

                foreach (var quarterGroup in quarterGroups)
                {
                    var quarter = quarterGroup.Key;

                    var nextQuarter = quarter.AddMinutes(15);

                    // Only store completed quarters
                    if (now < nextQuarter)
                        continue;

                    var values = quarterGroup
                            .Select(g => g.Value)
                            .Where(v => !double.IsNaN(v) && !double.IsInfinity(v))
                            .ToList();

                    if (values.Count < 1)
                        continue;

                    var averagePower = values.Average() / 4000; // kWh per kwartier

                    var entry = new InverterMeasurement
                    {
                        ProviderName = providerName,
                        InverterId = id,
                        Time = quarter,
                        SolarProductionKWh = averagePower
                    };

                    await _inverterMeasurementService.Add(new List<InverterMeasurement> { entry });

                    // Remove processed items from memory
                    foreach (var item in quarterGroup)
                    {
                        collection.Remove(item.Key);
                    }
                }
            }
        }
    }
}

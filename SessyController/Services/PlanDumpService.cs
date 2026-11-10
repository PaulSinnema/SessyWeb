using System.Text.Json;
using System.Text.Json.Serialization;
using SessyCommon;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Builds a single self-contained JSON snapshot of everything needed to analyse a plan for a
    /// given period: the planner settings, the raw EPEX prices and taxes it worked from, the planned
    /// quarters it produced, the actual quarters that followed, the measured facts, and every stored
    /// version of Charged's schedule for those quarters.
    ///
    /// Everything is assembled in memory and returned as a string — the caller streams it to the
    /// browser for download. Nothing is ever written to disk.
    /// </summary>
    public class PlanDumpService
    {
        private readonly PlannedQuarterDataService _plannedService;
        private readonly ActualQuarterDataService _actualService;
        private readonly EPEXPricesDataService _epexService;
        private readonly TaxesDataService _taxesService;
        private readonly SettingsDataService _settingsDataService;
        private readonly QuarterlyFactsService _factsService;
        private readonly InvestmentDataService _investmentService;
        private readonly SettingsService _settingsService;
        private readonly ChargedScheduleQuarterDataService _chargedScheduleService;

        public PlanDumpService(
            PlannedQuarterDataService plannedService,
            ActualQuarterDataService actualService,
            EPEXPricesDataService epexService,
            TaxesDataService taxesService,
            SettingsDataService settingsDataService,
            QuarterlyFactsService factsService,
            InvestmentDataService investmentService,
            SettingsService settingsService,
            ChargedScheduleQuarterDataService chargedScheduleService)
        {
            _plannedService = plannedService;
            _actualService = actualService;
            _epexService = epexService;
            _taxesService = taxesService;
            _settingsDataService = settingsDataService;
            _factsService = factsService;
            _investmentService = investmentService;
            _settingsService = settingsService;
            _chargedScheduleService = chargedScheduleService;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Assembles the plan dump for [start, end] and returns it as pretty-printed JSON.
        /// </summary>
        public async Task<string> BuildJsonAsync(DateTime start, DateTime end)
        {
            var planned = await _plannedService.GetList(async set =>
                await Task.FromResult(set.Where(p => p.Time >= start && p.Time <= end)
                                         .OrderBy(p => p.Time).ToList()));

            var actual = await _actualService.GetList(async set =>
                await Task.FromResult(set.Where(a => a.Time >= start && a.Time <= end)
                                         .OrderBy(a => a.Time).ToList()));

            var epex = await _epexService.GetList(async set =>
                await Task.FromResult(set.Where(e => e.Time >= start && e.Time <= end)
                                         .OrderBy(e => e.Time).ToList()));

            // Taxes are time-ranged periods, not per-quarter rows — dump them all so the price
            // build-up for the period can always be reconstructed.
            var taxes = await _taxesService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            var settingsList = await _settingsDataService.GetList(async set =>
                await Task.FromResult(set.ToList()));
            var settings = settingsList.FirstOrDefault();

            var measured = await _factsService.GetAsync(start, end);

            // Battery investments drive the derived cycle (wear) cost, which gates all arbitrage.
            var investments = await _investmentService.GetList(async set =>
                await Task.FromResult(set.ToList()));

            // All stored versions, so what Charged planned at any moment can be reconstructed.
            var chargedSchedule = await _chargedScheduleService.GetList(async set =>
                await Task.FromResult(set.Where(c => c.Time >= start && c.Time <= end)
                                         .OrderBy(c => c.Time).ThenBy(c => c.FetchedAt).ToList()));

            var dump = new
            {
                Meta = new
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    AppVersion = AppInfo.Version,
                    PeriodStart = start,
                    PeriodEnd = end,
                    PlannedCount = planned.Count,
                    ActualCount = actual.Count,
                    EpexPriceCount = epex.Count,
                    MeasuredCount = measured.Count,
                    InvestmentCount = investments.Count,
                    ChargedScheduleCount = chargedSchedule.Count,
                    DerivedCycleCostEurPerKWh = _settingsService.CycleCost,
                    Note = "In-memory plan dump for analysis. Times are in the configured local timezone. " +
                           "DerivedCycleCostEurPerKWh is the raw wear cost from the battery investments; " +
                           "the Balanced strategy multiplies it by 1.5 inside the planner. " +
                           "ChargedSchedule holds every changed version of Charged's planned power per quarter " +
                           "(W, negative = charging); Charged's plan at a moment is the newest row per Time with FetchedAt at or before it."
                },
                Settings = settings,
                Investments = investments,
                Taxes = taxes,
                EpexPrices = epex,
                PlannedQuarters = planned,
                ActualQuarters = actual,
                MeasuredFacts = measured,
                ChargedSchedule = chargedSchedule
            };

            return JsonSerializer.Serialize(dump, JsonOptions);
        }
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services.Statistics;
using SessyData.Services;

namespace SessyController.Services
{
    /// <summary>
    /// Runs all configuration checks and returns a list of results
    /// for display on the Tips & Checks tab in Settings.
    /// </summary>
    public class ConfigurationCheckService
    {
        private readonly IConfiguration _configuration;
        private readonly TaxesDataService _taxesDataService;
        private readonly IGasPricesDataService _gasPricesDataService;
        private readonly IEPEXPricesService _epexPricesService;
        private readonly TimeZoneService _timeZoneService;
        private readonly HeatPumpConfig _heatPumpConfig;
        private readonly IMilpService _milpService;

        public ConfigurationCheckService(
            IConfiguration configuration,
            TaxesDataService taxesDataService,
            IGasPricesDataService gasPricesDataService,
            IEPEXPricesService epexPricesService,
            TimeZoneService timeZoneService,
            IOptions<HeatPumpConfig> heatPumpConfig,
            IMilpService milpService)
        {
            _configuration = configuration;
            _taxesDataService = taxesDataService;
            _gasPricesDataService = gasPricesDataService;
            _epexPricesService = epexPricesService;
            _timeZoneService = timeZoneService;
            _heatPumpConfig = heatPumpConfig.Value;
            _milpService = milpService;
        }

        public async Task<List<ConfigurationCheck>> RunAllChecksAsync()
        {
            var checks = new List<ConfigurationCheck>();

            await CheckEneverToken(checks);
            await CheckTaxesConfiguration(checks);
            await CheckGasPricesHistory(checks);
            CheckHeatPumpConfiguration(checks);
            await CheckPlanStatus(checks).ConfigureAwait(false);

            return checks.OrderBy(c => c.Severity).ToList();
        }

        private Task CheckEneverToken(List<ConfigurationCheck> checks)
        {
            var token = _configuration["Enever:Token"];

            if (string.IsNullOrWhiteSpace(token))
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "Enever token not configured",
                    Description = "No live gas price feed available. Add your free Enever token to appsettings.json to enable daily TTF gas price fetching.",
                    ActionUrl = "https://enever.nl/token-aanmaken/",
                    ActionLabel = "Get free token"
                });
            }
            else if (!_epexPricesService.CurrentGasPriceEurPerM3.HasValue)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Enever token configured but no gas price fetched yet",
                    Description = "The token is set but no gas price has been received yet. This is normal on startup — the price is fetched once per day."
                });
            }
            else
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Live gas price active",
                    Description = $"Current TTF gas price (all-in): € {_epexPricesService.CurrentGasPriceEurPerM3.Value:F4}/m³."
                });
            }

            return Task.CompletedTask;
        }

        private async Task CheckTaxesConfiguration(List<ConfigurationCheck> checks)
        {
            var now = _timeZoneService.Now;
            var taxes = await _taxesDataService.GetTaxesForDate(now);

            if (taxes == null)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No taxes record found",
                    Description = "No applicable Taxes record exists. Energy price calculations will be incorrect. Add a Taxes record in Settings → Taxes.",
                    ActionLabel = "Go to Taxes"
                });
                return;
            }

            // Check gas supplier markup.
            if (taxes.GasSupplierMarkupEurPerM3 == 0.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Gas supplier markup is €0,00",
                    Description = "The supplier markup (leveranciersopslag) is not configured. The calculated gas price will be lower than your actual bill. " +
                                  "Check your energy contract for the supplier margin and enter it in Settings → Taxes.",
                    ActionLabel = "Go to Taxes"
                });
            }
            else
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Gas taxes configured",
                    Description = $"Energy tax: € {taxes.GasEnergyTaxEurPerM3:F4}/m³, supplier markup: € {taxes.GasSupplierMarkupEurPerM3:F4}/m³, VAT: {taxes.GasValueAddedTaxPct:F1}%."
                });
            }

            // Check electricity taxes completeness.
            if (taxes.EnergyTax == 0.0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Electricity energy tax is €0,00",
                    Description = "The electricity energy tax (energiebelasting) is not configured. Electricity price calculations may be incorrect.",
                    ActionLabel = "Go to Taxes"
                });
            }
        }

        private async Task CheckGasPricesHistory(List<ConfigurationCheck> checks)
        {
            var gasPrices = await _gasPricesDataService.GetAllAsync();

            if (!gasPrices.Any())
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "No gas price history",
                    Description = "No gas prices have been stored yet. The heating-degree-day weighted average cannot be calculated. " +
                                  "Prices are fetched daily — history will build up over time."
                });
            }
            else
            {
                var oldest = gasPrices.Min(g => g.Date);
                var newest = gasPrices.Max(g => g.Date);
                var days = gasPrices.Count;

                if (days < 30)
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Warning,
                        Title = $"Gas price history is short ({days} days)",
                        Description = $"Only {days} days of gas prices stored (since {oldest:dd-MM-yyyy}). " +
                                      "The weighted average will become more accurate as more data accumulates. " +
                                      "A full year gives the most representative seasonal weighting."
                    });
                }
                else
                {
                    checks.Add(new ConfigurationCheck
                    {
                        Severity = CheckSeverity.Info,
                        Title = $"Gas price history: {days} days",
                        Description = $"Gas prices stored from {oldest:dd-MM-yyyy} to {newest:dd-MM-yyyy}. " +
                                      "Heating-degree-day weighted average is active."
                    });
                }
            }
        }

        private void CheckHeatPumpConfiguration(List<ConfigurationCheck> checks)
        {
            if (!_heatPumpConfig.IsConfigured)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Heat pump not configured",
                    Description = "No HeatPumpConfig found in appsettings.json. Heat Pump Savings will not be shown. " +
                                  "If you have a heat pump, add HeatPumpConfig to enable savings tracking."
                });
                return;
            }

            // Check if configured gas price is being used as fallback.
            if (_heatPumpConfig.GasPriceEurPerM3 > 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "Heat pump configured gas price fallback",
                    Description = $"Configured fallback gas price: € {_heatPumpConfig.GasPriceEurPerM3:F4}/m³. " +
                                  "This is only used when no live Enever data or history is available."
                });
            }

            checks.Add(new ConfigurationCheck
            {
                Severity = CheckSeverity.Info,
                Title = "Heat pump configured",
                Description = $"Annual gas consumption: {_heatPumpConfig.AnnualGasConsumptionM3:F0} m³/year, " +
                              $"installed: {_heatPumpConfig.InstallationDate:dd-MM-yyyy}."
            });
        }

        private async Task CheckPlanStatus(List<ConfigurationCheck> checks)
        {
            var now = _timeZoneService.Now;
            var plan = await _milpService.GetPlanStatisticsAsync(now);

            if (plan.TotalFutureQuarters == 0)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = "No active MILP plan",
                    Description = "No battery plan is currently active. The batteries may be running without optimization. " +
                                  "Check if EPEX prices are available and the service is running."
                });
            }
            else if (plan.IsRestoredFromDb)
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = "Plan restored from database",
                    Description = $"The current plan was restored after a restart. " +
                                  $"It covers {plan.TotalFutureQuarters} future quarters until {plan.PlanHorizon:dd-MM-yyyy HH:mm}. " +
                                  "A fresh plan will be generated when new EPEX prices arrive."
                });
            }
            else
            {
                checks.Add(new ConfigurationCheck
                {
                    Severity = CheckSeverity.Info,
                    Title = "MILP plan active",
                    Description = $"Plan generated at {plan.LastBuildTime:dd-MM-yyyy HH:mm}, " +
                                  $"covering {plan.TotalFutureQuarters} quarters ({plan.TotalFutureQuarters / 4.0:F1} hrs). " +
                                  $"Expected profit: € {plan.ExpectedProfitEur:F2}."
                });
            }
        }
    }
}
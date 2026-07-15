using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using SessyCommon.Extensions;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Pages;

namespace SessyWeb.Components
{
    /// <summary>
    /// Dropdown-friendly wrapper around PlanHistoryEntry — the dropdown needs a single
    /// human-readable label, which we don't want to compute inside a Razor expression.
    /// </summary>
    public sealed class PlanHistoryOption
    {
        public Guid PlanId { get; init; }
        public string Label { get; init; } = string.Empty;
    }

    /// <summary>One point of a historical plan's power line, in the same visual scale as the
    /// live Planned charge/discharge series (Watts / 18000) so it overlays them directly.</summary>
    public sealed class PlanVisualPoint
    {
        public DateTime Time { get; init; }
        public double PlanPowerVisual { get; init; }
    }

    public partial class ChargingHoursChartComponent : BaseComponent
    {
        [Inject]
        private TaxesDataService? _taxesDataService { get; set; }

        [Inject]
        private PlannedActionDataService? _plannedActionDataService { get; set; }

        [Parameter]
        public List<QuarterlyInfoView> QuarterlyInfos { get; set; } = new();

        [Parameter]
        public bool ShowAll { get; set; }

        // Annotation data point — only set when the exact current quarter exists in the series.
        // Must be a reference to an item from QuarterlyInfos so Radzen can position it correctly.
        public QuarterlyInfoView? NowQuarter { get; private set; }

        [Parameter]
        public string? GraphStyle { get; set; }

        public RadzenChart? QuarterlyHourChart { get; set; }

        public bool ShowSellingPriceLabels { get; set; }

        public double ChartMin => -ChartMinMax;
        public double ChartMax => ChartMinMax;

        // ── Plan history overlay ──────────────────────────────────────────────
        // Follows "Show all" — no separate toggle. Off by default (Show all is off by default).
        public bool ShowPlanHistory { get; set; }
        public List<PlanHistoryOption> AvailablePlans { get; private set; } = new();
        public Guid? SelectedPlanId { get; private set; }
        public List<PlanVisualPoint> PlanPoints { get; private set; } = new();

        private DateTime _loadedPlanWindowFrom;
        private DateTime _loadedPlanWindowTo;
        private bool _havePlanWindow;

        /// <summary>
        /// Called whenever "Show all" or its date window changes. Plan history mirrors "Show all"
        /// exactly: on when Show all is on, listing every plan created within the same [from, to)
        /// window Show all currently displays.
        /// </summary>
        public async Task SetPlanHistoryWindowAsync(bool showAll, DateTime from, DateTime to)
        {
            ShowPlanHistory = showAll;

            if (!showAll)
                return; // Keep any cached AvailablePlans/PlanPoints — just hidden while off.

            bool windowChanged = !_havePlanWindow || from != _loadedPlanWindowFrom || to != _loadedPlanWindowTo;
            if (!windowChanged)
                return;

            _loadedPlanWindowFrom = from;
            _loadedPlanWindowTo = to;
            _havePlanWindow = true;

            await LoadAvailablePlansAsync(from, to).ConfigureAwait(false);
        }

        private async Task LoadAvailablePlansAsync(DateTime from, DateTime to)
        {
            if (_plannedActionDataService == null) return;

            var history = await _plannedActionDataService
                .GetPlanHistoryAsync(maxEntries: 200, since: from, until: to)
                .ConfigureAwait(false);

            AvailablePlans = history
                .Select(h => new PlanHistoryOption
                {
                    PlanId = h.PlanId,
                    Label = $"{h.SavedAt:dd-MM HH:mm} — {h.Reason}"
                })
                .ToList();

            // Keep the current selection if it's still in the refreshed list, else pick the newest.
            if (SelectedPlanId == null || !AvailablePlans.Any(p => p.PlanId == SelectedPlanId))
            {
                if (AvailablePlans.Count > 0)
                    await OnPlanSelected(AvailablePlans[0].PlanId).ConfigureAwait(false);
                else
                    await OnPlanSelected(null).ConfigureAwait(false);
            }
        }

        public async Task OnPlanSelected(Guid? planId)
        {
            SelectedPlanId = planId;

            if (planId == null || _plannedActionDataService == null)
            {
                PlanPoints = new List<PlanVisualPoint>();
                return;
            }

            var actions = await _plannedActionDataService.GetPlanAsync(planId.Value).ConfigureAwait(false);

            PlanPoints = actions
                .Select(a => new PlanVisualPoint
                {
                    Time = a.Time,
                    PlanPowerVisual = PlanPowerVisualFor(a.Mode, a.PowerW)
                })
                .ToList();
        }

        // Same scale and sign convention as QuarterlyInfoView.PlannedChargePowerVisual /
        // PlannedDischargePowerVisual: charging renders below zero, discharging above.
        private static double PlanPowerVisualFor(string mode, double powerW) =>
            mode switch
            {
                "Charging" => -(powerW / 18000.0),
                "Discharging" => powerW / 18000.0,
                _ => 0.0
            };

        private string GetBuyingPriceFill(object obj)
        {
            var qi = (QuarterlyInfo)obj;
            return qi.IsPriceExpected ? "#324ab255" : "#324ab2";
        }

        private string GetSellingPriceFill(object obj)
        {
            var qi = (QuarterlyInfo)obj;
            return qi.IsPriceExpected ? "#00aae455" : "#00aae4";
        }

        private bool IsExpected(List<QuarterlyInfoView> quarterlyInfos)
        {
            return quarterlyInfos != null && quarterlyInfos.Any(q => q.IsPriceExpected);
        }

        public double ChartMinMax
        {
            get
            {
                if (QuarterlyInfos == null || QuarterlyInfos.Count == 0)
                    return 1.0;

                var max = QuarterlyInfos.Max(qi => qi.Price);
                var min = QuarterlyInfos.Min(qi => qi.Price);

                return Math.Round(Math.Max(max, Math.Abs(min)) + 0.20, 1);
            }
        }

        protected override async Task OnParametersSetAsync()
        {
            var now = _timeZoneService!.Now;

            var taxes = await _taxesDataService!.GetTaxesForDate(now).ConfigureAwait(false);
            if (taxes != null)
                ShowSellingPriceLabels = !taxes.Netting;

            // Find the exact current quarter in the series and set its NowLineHeight to ChartMax
            // so the vertical bar spans the full chart height.
            NowQuarter = QuarterlyInfos.FirstOrDefault(q => q.Time == now.DateFloorQuarter());
            if (NowQuarter != null)
                NowQuarter.NowLineHeight = ChartMax;
        }
    }
}
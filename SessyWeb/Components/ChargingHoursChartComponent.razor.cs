using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyController.Services.Items;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Helpers;
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

        [Inject]
        private ChargedScheduleService? _chargedScheduleService { get; set; }

        /// <summary>When Charged's schedule was last read from the batteries; null while none arrived.</summary>
        public DateTime? ChargedScheduleFetchedAt => _chargedScheduleService?.LastFetched;

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

        /// <summary>
        /// The subset of QuarterlyInfos that carries a real power reading — a hardware value for
        /// the executing quarter, a stored measurement for past ones. The actual-power series
        /// binds to this so it simply ends at "now" instead of tracing the plan a second time.
        ///
        /// A filtered list rather than null/NaN values on the full set: Radzen resolves
        /// ValueProperty through a non-nullable getter in CartesianSeries.DataAt/TooltipY, which
        /// the crosshair calls for the nearest point by X regardless of whether that point is
        /// drawn — a null there throws while merely moving the mouse.
        /// </summary>
        public List<QuarterlyInfoView> ActualPowerPoints { get; private set; } = new();

        /// <summary>
        /// True when the batteries returned a schedule of their own for this window. They keep
        /// planning whoever is executing, so this is normally true in both directions; it stays
        /// false while the schedule could not be read.
        /// </summary>
        public bool HasChargedSchedule { get; private set; }

        /// <summary>
        /// Whether Charged's series are drawn at all. Off by default: the comparison is something
        /// you ask for, and six extra series make the chart hard to read the rest of the time.
        /// With it off the chart shows our own plan as filled areas whoever is executing, so it
        /// never falls back to nothing but faint dashed lines.
        /// </summary>
        public bool ShowCharged { get; private set; }

        public void ToggleCharged(bool value)
        {
            if (ShowCharged == value) return;

            ShowCharged = value;

            // ShouldRender gates on _dirty, so without this the checkbox does nothing visible.
            MarkDirty();
        }

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
            if (ShowPlanHistory != showAll)
                MarkDirty();

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

            MarkDirty();

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
            MarkDirty();

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

        // ── Render gating ─────────────────────────────────────────────────────
        // This chart draws roughly 17 series over up to 288 quarters, so a redraw is thousands of
        // SVG nodes to diff and ship over SignalR. The page above it re-renders on a timer, which
        // used to drag the whole chart along every few seconds, on every open tab.

        private List<QuarterlyInfoView>? _renderedSeries;
        private string? _renderedGraphStyle;
        private bool _renderedShowAll;
        private DateTime _renderedQuarter;
        private bool _renderedChargedInControl;
        private DateTime? _renderedScheduleFetchedAt;
        private bool _dirty = true;

        /// <summary>Marks the chart as needing a redraw; the plan-history paths call it directly.</summary>
        private void MarkDirty() => _dirty = true;

        [Inject] private ILogger<ChargingHoursChartComponent>? _logger { get; set; }

        private RenderTimer? _renderTimerInstance;

        private RenderTimer _renderTimer =>
            _renderTimerInstance ??= new RenderTimer(
                nameof(ChargingHoursChartComponent), message => _logger?.LogInformation(message));

        protected override bool ShouldRender()
        {
            if (!_dirty) return false;

            _dirty = false;
            _renderTimer.Started();
            return true;
        }

        protected override void OnAfterRender(bool firstRender) => _renderTimer.Finished();

        protected override async Task OnParametersSetAsync()
        {
            var now = _timeZoneService!.Now;
            var nowQuarter = now.DateFloorQuarter();

            bool changed = !ReferenceEquals(_renderedSeries, QuarterlyInfos)
                        || _renderedGraphStyle != GraphStyle
                        || _renderedShowAll != ShowAll
                        || _renderedQuarter != nowQuarter
                        || _renderedChargedInControl != ChargedInControl
                        || _renderedScheduleFetchedAt != ChargedScheduleFetchedAt;

            if (!changed) return;

            _renderedSeries = QuarterlyInfos;
            _renderedGraphStyle = GraphStyle;
            _renderedShowAll = ShowAll;
            _renderedQuarter = nowQuarter;
            _renderedChargedInControl = ChargedInControl;
            _renderedScheduleFetchedAt = ChargedScheduleFetchedAt;
            MarkDirty();

            ActualPowerPoints = QuarterlyInfos.Where(q => q.HasActualPower).ToList();

            // Computed here rather than in the markup: the series binds it on every render, and a
            // scan per render is exactly what the render gating in this component exists to avoid.
            HasChargedSchedule = QuarterlyInfos.Any(q => q.HasChargedPlan);

            // Taxes only change per day; asking the database on every parameter set meant a query
            // per page render.
            if (_taxesForDate != now.Date)
            {
                var taxes = await _taxesDataService!.GetTaxesForDate(now).ConfigureAwait(false);
                if (taxes != null)
                    ShowSellingPriceLabels = !taxes.Netting;

                _taxesForDate = now.Date;
            }

            // Find the exact current quarter in the series and set its NowLineHeight to ChartMax
            // so the vertical bar spans the full chart height.
            NowQuarter = QuarterlyInfos.FirstOrDefault(q => q.Time == nowQuarter);
            if (NowQuarter != null)
                NowQuarter.NowLineHeight = ChartMax;
        }

        private DateTime _taxesForDate = DateTime.MinValue;
    }
}
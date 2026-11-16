using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>Full cycles per month for one series, stacked by source, with the running total.</summary>
    public partial class CycleChartComponent : ComponentBase
    {
        [Parameter, EditorRequired]
        public CycleSeries Series { get; set; } = default!;

        [Parameter]
        public bool WithEstimate { get; set; } = true;

        // New key per series and toggle state, so the chart recomputes its axes.
        private string ChartKey => $"{Series.Name}|{WithEstimate}";

        private string CumulativeProperty => WithEstimate
            ? nameof(CycleMonth.CumulativeCycles)
            : nameof(CycleMonth.CumulativeCyclesWithoutEstimate);
    }
}

using Microsoft.AspNetCore.Components;

namespace SessyWeb.Components
{
    /// <summary>The card around a statistic: title with info tip, refresh button, spinner, empty text and content.</summary>
    public partial class StatisticsCard : ComponentBase
    {
        [Parameter] public string? Title { get; set; }

        [Parameter] public string? Info { get; set; }

        /// <summary>Shows the refresh button when set.</summary>
        [Parameter] public EventCallback OnRefresh { get; set; }

        [Parameter] public string RefreshTitle { get; set; } = "Reload";

        /// <summary>Extra controls in the header (period choice and the like).</summary>
        [Parameter] public RenderFragment? HeaderContent { get; set; }

        /// <summary>Text shown above the spinner and the content, also while loading.</summary>
        [Parameter] public RenderFragment? Intro { get; set; }

        [Parameter] public bool Loading { get; set; }

        [Parameter] public bool IsEmpty { get; set; }

        [Parameter] public RenderFragment? EmptyContent { get; set; }

        [Parameter] public RenderFragment? ChildContent { get; set; }
    }
}

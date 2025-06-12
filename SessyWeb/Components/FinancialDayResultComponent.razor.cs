using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyController.Services;
using SessyController.Services.Items;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Components
{
    public partial class FinancialDayResultComponent : BaseComponent
    {
        [Inject]
        public FinancialResultsService? _financialResultsService { get; set; }
        [Parameter]
        public List<FinancialResult>? FinancialResultsList { get; set; }

        RadzenDataGrid<FinancialResult>? financialResultsGrid { get; set; }

        [Parameter]
        public bool ExpandAllGroups { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await financialResultsGrid!.FirstPage();
        }

        void OnRender(DataGridRenderEventArgs<FinancialResult> args)
        {
            if (args.FirstRender)
            {
                args.Grid.Groups.Add(new GroupDescriptor() { Property = nameof(FinancialResult.YearMonth), Title = "Time" });
                StateHasChanged();
            }
        }

        public decimal GetDailyTotalCost(IEnumerable<FinancialResult> items)
        {
            return items.Sum(fr => fr.Cost);
        }
    }
}


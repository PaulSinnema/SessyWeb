using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class EnergyHistoryPage : PageBase
    {
        [Inject]
        private QuarterlyMeasurementDataService? _measurementService { get; set; }

        [Inject]
        private TimeZoneService? _timeZoneService { get; set; }

        private List<QuarterlyMeasurement>? MeasurementList { get; set; }

        RadzenDataGrid<QuarterlyMeasurement>? energyGrid { get; set; }

        int count { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await energyGrid!.FirstPage();
        }

        async Task LoadData(LoadDataArgs args)
        {
            IsBusy = true;

            try
            {
                EnsureEnergyGrid();

                MeasurementList = await _measurementService!.GetList(async (set) =>
                {
                    var result = set
                        .OrderByDescending(m => m.Time)
                        .AsQueryable();

                    var query = await Task.FromResult(result);

                    if (!string.IsNullOrEmpty(args.Filter))
                        query = query.Where(energyGrid!.ColumnsCollection);

                    if (!string.IsNullOrEmpty(args.OrderBy))
                        query = query.OrderBy(args.OrderBy);

                    count = query.Count();

                    return query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
                });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void EnsureEnergyGrid()
        {
            if (energyGrid == null)
                throw new InvalidOperationException($"{nameof(energyGrid)} cannot be null here.");
        }
    }
}
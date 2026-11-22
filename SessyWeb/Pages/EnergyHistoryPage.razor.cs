using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;
using SessyWeb.Helpers;

namespace SessyWeb.Pages
{
    public partial class EnergyHistoryPage : PageBase
    {
        [Inject]
        private QuarterlyMeasurementDataService? _measurementService { get; set; }

        [Inject]
        private EnergyHistoryDataService? _energyHistoryService { get; set; }

        private List<QuarterlyMeasurement>? MeasurementList { get; set; }
        private List<EnergyHistory>? MeterReadingsList { get; set; }

        RadzenDataGrid<QuarterlyMeasurement>? energyGrid { get; set; }
        RadzenDataGrid<EnergyHistory>? meterGrid { get; set; }

        int count { get; set; }
        int meterCount { get; set; }

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

                    var (page, total) = query.ToGridPage(args, energyGrid!);
                    count = total;

                    return page;
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

        async Task LoadMeterReadings(LoadDataArgs args)
        {
            IsBusy = true;

            try
            {
                if (meterGrid == null)
                    throw new InvalidOperationException($"{nameof(meterGrid)} cannot be null here.");

                MeterReadingsList = await _energyHistoryService!.GetList(async (set) =>
                {
                    var result = set
                        .OrderByDescending(h => h.Time)
                        .AsQueryable();

                    var query = await Task.FromResult(result);

                    var (page, total) = query.ToGridPage(args, meterGrid!);
                    meterCount = total;

                    return page;
                });
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
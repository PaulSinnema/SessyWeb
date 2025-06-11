using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;


namespace SessyWeb.Pages
{
    public partial class TaxesPage
    {
        [Inject]
        TimeZoneService? _timezoneService { get; set; }

        [Inject]
        private TaxesDataService? _taxesService { get; set; }

        private List<Taxes>? TaxesList { get; set; } = new();

        RadzenDataGrid<Taxes>? taxesGrid { get; set; }

        public bool isLoading { get; set; } = false;

        int count { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            AddRowFor2025();

            if (firstRender)
                await taxesGrid!.FirstPage(true);
        }

        private void AddRowFor2025()
        {
            var list = _taxesService!.GetList((set => { return set.ToList(); }));

            if (list.Count == 0)
            {
                _taxesService.Add(new List<Taxes>
                                        {
                                            new Taxes
                                            {
                                                Time = new DateTime(2025, 1, 1, 0, 0, 0),
                                                EnergyTax = 0.10154,
                                                ValueAddedTax = 21.0,
                                                TaxReduction = 635.19,
                                                PurchaseCompensation = 0.01815, // Frank Energie
                                                ReturnDeliveryCompensation = 0.012705 // Frank Energie
                                            }
                                        },
                                    (tax, set) => { return false; });

                taxesGrid!.Reload();
            }
        }

        async Task LoadData(LoadDataArgs args)
        {
            isLoading = true;

            await Task.Yield();

            EnsureTaxesGrid();

            var filter = taxesGrid!.ColumnsCollection;

            TaxesList = _taxesService!.GetList((set) =>
            {
                var query = set
                    .OrderBy(eh => eh.Time)
                    .AsQueryable();

                if (!string.IsNullOrEmpty(args.Filter))
                {
                    query = query.Where(taxesGrid.ColumnsCollection);
                }

                if (!string.IsNullOrEmpty(args.OrderBy))
                {
                    query = query.OrderBy(args.OrderBy);
                }

                count = query.Count();

                return query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList();
            });

            isLoading = false;
        }

        private void EnsureTaxesGrid()
        {
            if (taxesGrid == null) throw new InvalidOperationException($"{nameof(taxesGrid)} can not be null here, did you forget a @ref?");
        }

        async Task EditRow(Taxes taxes)
        {
            if (!taxesGrid!.IsValid) return;

            await taxesGrid!.EditRow(taxes);
        }

        void OnUpdateRow(Taxes taxes)
        {
            List<Taxes> list = new List<Taxes> { taxes };

            _taxesService!.Update(list, (item, set) => set.Where(eh => eh.Id == taxes.Id).FirstOrDefault());
        }

        async Task SaveRow(Taxes taxes)
        {
            await taxesGrid!.UpdateRow(taxes);
        }

        async Task CancelEdit(Taxes taxes)
        {
            taxesGrid!.CancelEditRow(taxes);

            await taxesGrid.Reload();
        }

        async Task DeleteRow(Taxes taxes)
        {
            if(taxes.Id != 0)
                _taxesService!.Remove(new List<Taxes> { taxes }, (item, set) => set.Where(eh => eh.Id == item.Id).FirstOrDefault());

            await taxesGrid!.Reload();
        }

        async Task InsertRow()
        {
            if (!taxesGrid!.IsValid) return;

            var taxes = new Taxes { Time = _timezoneService!.Now.Date };
            await taxesGrid.InsertRow(taxes);
            count++;
        }

        async Task InsertAfterRow(Taxes row)
        {
            if (!taxesGrid!.IsValid) return;

            var taxes = new Taxes();
            await taxesGrid.InsertAfterRow(taxes, row);
            count++;
        }

        void OnCreateRow(Taxes taxes)
        {
            _taxesService!.Add(new List<Taxes> { taxes }, (item, set) => set.Where(eh => eh.Id == item.Id).FirstOrDefault());
        }
    }
}
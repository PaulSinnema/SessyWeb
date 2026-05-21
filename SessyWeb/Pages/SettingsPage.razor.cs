using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyController.Services;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class SettingsPage : PageBase
    {
        // ── Services ──────────────────────────────────────────────────────────

        [Inject] private InvestmentGroupDataService? _groupService { get; set; }
        [Inject] private InvestmentDataService? _investmentService { get; set; }
        [Inject] private TaxesDataService? _taxesService { get; set; }
        [Inject] private SessyWebControlDataService? _controlService { get; set; }
        [Inject] private TimeZoneService? _timeZoneService { get; set; }

        // ── Investment Groups ─────────────────────────────────────────────────

        private List<InvestmentGroup>? GroupList { get; set; } = new();
        private IEnumerable<InvestmentCategory> Categories => Enum.GetValues<InvestmentCategory>();
        RadzenDataGrid<InvestmentGroup>? groupsGrid { get; set; }
        private bool isGroupLoading { get; set; }
        private int groupCount { get; set; }

        async Task LoadGroupData(LoadDataArgs args)
        {
            IsBusy = true;
            try
            {
                isGroupLoading = true;
                await Task.Yield();

                GroupList = await _groupService!.GetList(async set =>
                {
                    var query = set.OrderBy(g => g.Name).AsQueryable();

                    if (!string.IsNullOrEmpty(args.Filter))
                        query = query.Where(groupsGrid!.ColumnsCollection);

                    if (!string.IsNullOrEmpty(args.OrderBy))
                        query = query.OrderBy(args.OrderBy);

                    groupCount = query.Count();

                    if (args.Skip.HasValue && args.Top.HasValue)
                        return await Task.FromResult(query.Skip(args.Skip.Value).Take(args.Top.Value).ToList());

                    return await Task.FromResult(query.ToList());
                });
            }
            finally { isGroupLoading = false; IsBusy = false; }
        }

        async Task InsertGroup()
        {
            if (!groupsGrid!.IsValid) return;
            await groupsGrid.InsertRow(new InvestmentGroup());
            groupCount++;
        }

        async Task EditGroup(InvestmentGroup group)
        {
            if (!groupsGrid!.IsValid) return;
            await groupsGrid!.EditRow(group);
        }

        async Task OnUpdateGroup(InvestmentGroup group) =>
            await _groupService!.Update(new List<InvestmentGroup> { group },
                (item, set) => set.FirstOrDefault(g => g.Id == group.Id));

        async Task SaveGroup(InvestmentGroup group) => await groupsGrid!.UpdateRow(group);

        async Task CancelGroupEdit(InvestmentGroup group)
        {
            groupsGrid!.CancelEditRow(group);
            await groupsGrid.Reload();
        }

        async Task DeleteGroup(InvestmentGroup group)
        {
            if (group.Id != 0)
                await _groupService!.Remove(new List<InvestmentGroup> { group },
                    (item, set) => set.FirstOrDefault(g => g.Id == item.Id));
            await groupsGrid!.Reload();
        }

        async Task OnCreateGroup(InvestmentGroup group) =>
            await _groupService!.Add(new List<InvestmentGroup> { group },
                (item, set) => set.Contains(group));

        // ── Investments ───────────────────────────────────────────────────────

        private List<Investment>? InvestmentList { get; set; } = new();
        private List<InvestmentGroup> Groups { get; set; } = new();
        RadzenDataGrid<Investment>? investmentsGrid { get; set; }
        private bool isInvestmentLoading { get; set; }
        private int investmentCount { get; set; }

        // ── Initialisation flags — set to true after FirstPage is called ──────
        private bool _groupsInitialised;
        private bool _investmentsInitialised;
        private bool _taxesInitialised;
        private bool _controlInitialised;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                Groups = await _groupService!.GetList(async set =>
                    await Task.FromResult(set.OrderBy(g => g.Name).ToList()));
            }

            // Each tab's grid ref becomes non-null the first time that tab renders.
            // Call FirstPage exactly once per grid using the initialisation flags.
            if (groupsGrid != null && !_groupsInitialised)
            {
                _groupsInitialised = true;
                await groupsGrid.FirstPage(true);
            }

            if (investmentsGrid != null && !_investmentsInitialised)
            {
                _investmentsInitialised = true;
                await investmentsGrid.FirstPage(true);
            }

            if (taxesGrid != null && !_taxesInitialised)
            {
                _taxesInitialised = true;
                await taxesGrid.FirstPage(true);
            }

            if (controlGrid != null && !_controlInitialised)
            {
                _controlInitialised = true;
                await controlGrid.FirstPage();
            }

            await base.OnAfterRenderAsync(firstRender);
        }

        async Task LoadInvestmentData(LoadDataArgs args)
        {
            IsBusy = true;
            try
            {
                isInvestmentLoading = true;
                await Task.Yield();

                InvestmentList = await _investmentService!.GetList(async set =>
                {
                    var query = set.OrderBy(i => i.PurchaseDate).AsQueryable();

                    if (!string.IsNullOrEmpty(args.Filter))
                        query = query.Where(investmentsGrid!.ColumnsCollection);

                    if (!string.IsNullOrEmpty(args.OrderBy))
                        query = query.OrderBy(args.OrderBy);

                    investmentCount = query.Count();

                    if (args.Skip.HasValue && args.Top.HasValue)
                        return await Task.FromResult(query.Skip(args.Skip.Value).Take(args.Top.Value).ToList());

                    return await Task.FromResult(query.ToList());
                });
            }
            finally { isInvestmentLoading = false; IsBusy = false; }
        }

        async Task InsertInvestment()
        {
            if (!investmentsGrid!.IsValid) return;
            await investmentsGrid.InsertRow(new Investment
            {
                PurchaseDate = _timeZoneService!.Now.Date,
                ExpectedLifetimeYears = 25
            });
            investmentCount++;
        }

        async Task InsertInvestmentAfterRow(Investment row)
        {
            if (!investmentsGrid!.IsValid) return;
            await investmentsGrid.InsertAfterRow(new Investment
            {
                PurchaseDate = _timeZoneService!.Now.Date,
                ExpectedLifetimeYears = 25
            }, row);
            investmentCount++;
        }

        async Task EditInvestment(Investment investment)
        {
            if (!investmentsGrid!.IsValid) return;
            await investmentsGrid!.EditRow(investment);
        }

        async Task OnUpdateInvestment(Investment investment) =>
            await _investmentService!.Update(new List<Investment> { investment },
                (item, set) => set.FirstOrDefault(i => i.Id == investment.Id));

        async Task SaveInvestment(Investment investment) => await investmentsGrid!.UpdateRow(investment);

        async Task CancelInvestmentEdit(Investment investment)
        {
            investmentsGrid!.CancelEditRow(investment);
            await investmentsGrid.Reload();
        }

        async Task DeleteInvestment(Investment investment)
        {
            if (investment.Id != 0)
                await _investmentService!.Remove(new List<Investment> { investment },
                    (item, set) => set.FirstOrDefault(i => i.Id == item.Id));
            await investmentsGrid!.Reload();
        }

        async Task OnCreateInvestment(Investment investment) =>
            await _investmentService!.Add(new List<Investment> { investment },
                (item, set) => set.Contains(investment));

        // ── Taxes ─────────────────────────────────────────────────────────────

        private List<Taxes>? TaxesList { get; set; } = new();
        RadzenDataGrid<Taxes>? taxesGrid { get; set; }
        private bool isTaxLoading { get; set; }
        private int taxCount { get; set; }

        async Task LoadTaxData(LoadDataArgs args)
        {
            IsBusy = true;
            try
            {
                isTaxLoading = true;
                await Task.Yield();
                await EnsureDefaultTaxes();

                TaxesList = await _taxesService!.GetList(async set =>
                {
                    var query = set.OrderBy(t => t.Time).AsQueryable();

                    if (!string.IsNullOrEmpty(args.Filter))
                        query = query.Where(taxesGrid!.ColumnsCollection);

                    if (!string.IsNullOrEmpty(args.OrderBy))
                        query = query.OrderBy(args.OrderBy);

                    taxCount = query.Count();

                    if (args.Skip.HasValue && args.Top.HasValue)
                        return await Task.FromResult(query.Skip(args.Skip.Value).Take(args.Top.Value).ToList());

                    return await Task.FromResult(query.ToList());
                });
            }
            finally { isTaxLoading = false; IsBusy = false; }
        }

        private async Task EnsureDefaultTaxes()
        {
            var list = await _taxesService!.GetList(async set =>
                await Task.FromResult(set.ToList()));

            if (list.Count == 0)
            {
                await _taxesService.Add(new List<Taxes>
                {
                    new Taxes
                    {
                        Time = new DateTime(2025, 1, 1, 0, 0, 0),
                        EnergyTax = 0.10154,
                        ValueAddedTax = 21.0,
                        TaxReduction = 635.19,
                        PurchaseCompensation = 0.01815,
                        ReturnDeliveryCompensation = 0.012705
                    }
                }, (tax, set) => { return false; });

                await taxesGrid!.Reload();
            }
        }

        async Task InsertTax()
        {
            if (!taxesGrid!.IsValid) return;
            await taxesGrid.InsertRow(new Taxes { Time = _timeZoneService!.Now.Date });
            taxCount++;
        }

        async Task InsertTaxAfterRow(Taxes row)
        {
            if (!taxesGrid!.IsValid) return;
            await taxesGrid.InsertAfterRow(new Taxes(), row);
            taxCount++;
        }

        async Task EditTax(Taxes tax)
        {
            if (!taxesGrid!.IsValid) return;
            await taxesGrid!.EditRow(tax);
        }

        async Task OnUpdateTax(Taxes tax) =>
            await _taxesService!.Update(new List<Taxes> { tax },
                (item, set) => set.FirstOrDefault(t => t.Id == tax.Id));

        async Task SaveTax(Taxes tax) => await taxesGrid!.UpdateRow(tax);

        async Task CancelTaxEdit(Taxes tax)
        {
            taxesGrid!.CancelEditRow(tax);
            await taxesGrid.Reload();
        }

        async Task DeleteTax(Taxes tax)
        {
            if (tax.Id != 0)
                await _taxesService!.Remove(new List<Taxes> { tax },
                    (item, set) => set.FirstOrDefault(t => t.Id == item.Id));
            await taxesGrid!.Reload();
        }

        async Task OnCreateTax(Taxes tax) =>
            await _taxesService!.Add(new List<Taxes> { tax },
                (item, set) => set.Contains(tax));

        // ── Who's in control ──────────────────────────────────────────────────

        private List<SessyWebControl>? ControlList { get; set; }
        RadzenDataGrid<SessyWebControl>? controlGrid { get; set; }
        private int controlCount { get; set; }

        async Task LoadControlData(LoadDataArgs args)
        {
            IsBusy = true;
            try
            {
                ControlList = await _controlService!.GetList(async set =>
                {
                    var query = set.OrderBy(c => c.Time).AsQueryable();

                    if (!string.IsNullOrEmpty(args.Filter))
                        query = query.Where(controlGrid!.ColumnsCollection);

                    if (!string.IsNullOrEmpty(args.OrderBy))
                        query = query.OrderBy(args.OrderBy);

                    controlCount = query.Count();

                    if (args.Skip.HasValue && args.Top.HasValue)
                        return await Task.FromResult(query.Skip(args.Skip.Value).Take(args.Top.Value).ToList());

                    return await Task.FromResult(query.ToList());
                });
            }
            finally { IsBusy = false; }
        }
    }
}
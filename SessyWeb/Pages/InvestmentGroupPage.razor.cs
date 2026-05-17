using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using SessyData.Model;
using SessyData.Services;
using System.Linq.Dynamic.Core;

namespace SessyWeb.Pages
{
    public partial class InvestmentGroupPage : PageBase
    {
        [Inject]
        private InvestmentGroupDataService? _groupService { get; set; }

        private List<InvestmentGroup>? GroupList { get; set; } = new();

        private IEnumerable<InvestmentCategory> Categories =>
            Enum.GetValues<InvestmentCategory>();

        RadzenDataGrid<InvestmentGroup>? groupsGrid { get; set; }

        public bool isLoading { get; set; } = false;

        int count { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await groupsGrid!.FirstPage(true);
        }

        async Task LoadData(LoadDataArgs args)
        {
            IsBusy = true;
            try
            {
                isLoading = true;
                await Task.Yield();

                GroupList = await _groupService!.GetList(async set =>
                {
                    var query = set.OrderBy(g => g.Name).AsQueryable();

                    if (!string.IsNullOrEmpty(args.Filter))
                        query = query.Where(groupsGrid!.ColumnsCollection);

                    if (!string.IsNullOrEmpty(args.OrderBy))
                        query = query.OrderBy(args.OrderBy);

                    count = query.Count();
                    return await Task.FromResult(
                        query.Skip(args.Skip!.Value).Take(args.Top!.Value).ToList());
                });

                isLoading = false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        async Task EditRow(InvestmentGroup group)
        {
            if (!groupsGrid!.IsValid) return;
            await groupsGrid!.EditRow(group);
        }

        async Task OnUpdateRow(InvestmentGroup group)
        {
            await _groupService!.Update(
                new List<InvestmentGroup> { group },
                (item, set) => set.FirstOrDefault(g => g.Id == group.Id));
        }

        async Task SaveRow(InvestmentGroup group) =>
            await groupsGrid!.UpdateRow(group);

        async Task CancelEdit(InvestmentGroup group)
        {
            groupsGrid!.CancelEditRow(group);
            await groupsGrid.Reload();
        }

        async Task DeleteRow(InvestmentGroup group)
        {
            if (group.Id != 0)
                await _groupService!.Remove(
                    new List<InvestmentGroup> { group },
                    (item, set) => set.FirstOrDefault(g => g.Id == item.Id));

            await groupsGrid!.Reload();
        }

        async Task InsertRow()
        {
            if (!groupsGrid!.IsValid) return;
            await groupsGrid.InsertRow(new InvestmentGroup());
            count++;
        }

        private async Task OnCreateRow(InvestmentGroup group)
        {
            await _groupService!.Add(
                new List<InvestmentGroup> { group },
                (item, set) => set.Contains(group));
        }
    }
}
using Radzen.Blazor;
using SessyData.Model;
using SessyData.Services;

namespace SessyWeb.Helpers
{
    /// <summary>
    /// Inline add/edit/delete for a Radzen data grid backed by a data service: the row actions the
    /// settings grids share, keyed on the entity's Id.
    /// </summary>
    public sealed class GridEditor<T> where T : class, IUpdatable<T>, new()
    {
        private readonly ServiceBase<T> _service;
        private readonly Func<RadzenDataGrid<T>?> _grid;
        private readonly Func<T> _newItem;
        private readonly Func<Task>? _afterChange;

        public GridEditor(ServiceBase<T> service, Func<RadzenDataGrid<T>?> grid, Func<T>? newItem = null, Func<Task>? afterChange = null)
        {
            _service = service;
            _grid = grid;
            _newItem = newItem ?? (() => new T());
            _afterChange = afterChange;
        }

        /// <summary>Total rows after filtering; bound to the grid's Count.</summary>
        public int Count { get; set; }

        private RadzenDataGrid<T> Grid => _grid() ?? throw new InvalidOperationException($"Grid for {typeof(T).Name} is not rendered yet.");

        public async Task Insert()
        {
            if (!Grid.IsValid) return;
            await Grid.InsertRow(_newItem());
            Count++;
        }

        public async Task InsertAfter(T row)
        {
            if (!Grid.IsValid) return;
            await Grid.InsertAfterRow(_newItem(), row);
            Count++;
        }

        public async Task Edit(T item)
        {
            if (!Grid.IsValid) return;
            await Grid.EditRow(item);
        }

        public async Task Save(T item) => await Grid.UpdateRow(item);

        public async Task Cancel(T item)
        {
            Grid.CancelEditRow(item);
            await Grid.Reload();
        }

        public async Task Delete(T item)
        {
            if (item.Id != 0)
                await _service.Remove([item], (i, set) => set.FirstOrDefault(s => s.Id == i.Id));
            await Grid.Reload();
            await AfterChange();
        }

        /// <summary>Grid RowCreate handler.</summary>
        public async Task OnCreate(T item)
        {
            await _service.Add([item], (i, set) => set.Contains(i));
            await AfterChange();
        }

        /// <summary>Grid RowUpdate handler.</summary>
        public async Task OnUpdate(T item)
        {
            await _service.Update([item], (i, set) => set.FirstOrDefault(s => s.Id == i.Id));
            await AfterChange();
        }

        private Task AfterChange() => _afterChange?.Invoke() ?? Task.CompletedTask;
    }
}

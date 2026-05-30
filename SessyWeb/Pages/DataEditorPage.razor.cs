using Microsoft.AspNetCore.Components;
using SessyController.Services;

namespace SessyWeb.Pages
{
    public partial class DataEditorPage : PageBase
    {
        [Inject] private DataEditorService? _editorService { get; set; }

        private List<TableInfo> _tables = [];
        private TableInfo? _selectedTable;
        private string? _whereClause;
        private EditorResult? _result;
        private string? _error;
        private bool _loading;
        private bool _saving;
        private int _page = 1;
        private const int PageSize = 20;
        private int TotalPages => _result == null ? 1 : Math.Max(1, (int)Math.Ceiling(_result.TotalCount / (double)PageSize));

        // ── Edit state ────────────────────────────────────────────────────────
        private string? _editingRowId;
        private Dictionary<string, string> _editValues = [];

        protected override void OnInitialized()
        {
            _tables = _editorService!.GetEditableTables();
            _selectedTable = _tables.FirstOrDefault();
        }

        private void OnTableChanged()
        {
            _result = null;
            _whereClause = null;
            _page = 1;
            CancelEdit();
        }

        private async Task LoadDataAsync()
        {
            if (_selectedTable == null) return;
            _loading = true;
            _error = null;
            CancelEdit();
            StateHasChanged();
            try
            {
                _result = await _editorService!.LoadAsync(_selectedTable, _whereClause, _page, PageSize);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                _loading = false;
                StateHasChanged();
            }
        }

        private async Task GoToPage(int page)
        {
            _page = page;
            await LoadDataAsync();
        }

        // ── Edit helpers ──────────────────────────────────────────────────────

        private void StartEdit(Dictionary<string, object?> row)
        {
            _editingRowId = row.TryGetValue("Id", out var id) ? id?.ToString() : null;
            _editValues = row.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
        }

        private void CancelEdit()
        {
            _editingRowId = null;
            _editValues = [];
        }

        private string GetEditValue(string col)
            => _editValues.TryGetValue(col, out var v) ? v : "";

        private void SetEditValue(string col, string value)
            => _editValues[col] = value;

        private async Task SaveRowAsync(Dictionary<string, object?> originalRow)
        {
            if (_selectedTable == null) return;
            _saving = true;
            _error = null;
            StateHasChanged();
            try
            {
                // Build updated row from edit values.
                var updatedRow = new Dictionary<string, object?>(
                    _editValues.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));

                var error = await _editorService!.SaveRowAsync(_selectedTable, updatedRow);
                if (error != null)
                {
                    _error = error;
                }
                else
                {
                    // Update display row in-place.
                    foreach (var kv in _editValues)
                        originalRow[kv.Key] = kv.Value;

                    CancelEdit();
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }
}
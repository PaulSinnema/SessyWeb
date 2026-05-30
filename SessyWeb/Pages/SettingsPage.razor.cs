using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Services.Statistics;
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
        [Inject] private ConfigurationCheckService? _checkService { get; set; }

        // ── Tips & Checks ─────────────────────────────────────────────────────

        private List<ConfigurationCheck>? _checks;
        private bool _checksInitialised;

        private string CheckColor(CheckSeverity severity) => severity switch
        {
            CheckSeverity.Error => "var(--rz-danger)",
            CheckSeverity.Warning => "var(--rz-warning)",
            CheckSeverity.Info => "var(--rz-success)",
            _ => "var(--rz-base-600)"
        };

        private async Task LoadChecks()
        {
            _checks = await _checkService!.RunAllChecksAsync();
            StateHasChanged();
        }

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

            // Load management settings the first time the tab renders.
            if (!_settingsInitialised)
            {
                _settingsInitialised = true;
                await LoadSettingsAsync();
            }

            // Load checks the first time the Tips & Checks tab renders.
            if (!_checksInitialised)
            {
                _checksInitialised = true;
                await LoadChecks();
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

        // ── Management Settings ───────────────────────────────────────────────

        [Inject] private SettingsDataService? _settingsDataService { get; set; }
        [Inject] private SettingsService? _settingsService { get; set; }

        private Settings? _settings;
        private bool _settingsSaving;
        private bool _settingsSaved;
        private bool _settingsInitialised;

        // Multi-select bindings for manual hours.
        private IEnumerable<int> _chargingHours = [];
        private IEnumerable<int> _dischargingHours = [];
        private IEnumerable<int> _nzhHours = [];

        // Monthly energy needs array (12 entries).
        private double[] _energyNeeds = new double[12];

        private static readonly IEnumerable<int> _allHours = Enumerable.Range(0, 24);

        private void OnChargingHoursChanged()
        {
            // Remove hours claimed by other lists.
            _chargingHours = _chargingHours.Except(_dischargingHours).Except(_nzhHours).ToList();
            _settings!.ManualChargingHoursArray = _chargingHours.ToArray();
        }

        private void OnDischargingHoursChanged()
        {
            _dischargingHours = _dischargingHours.Except(_chargingHours).Except(_nzhHours).ToList();
            _settings!.ManualDischargingHoursArray = _dischargingHours.ToArray();
        }

        private void OnNzhHoursChanged()
        {
            _nzhHours = _nzhHours.Except(_chargingHours).Except(_dischargingHours).ToList();
            _settings!.ManualNetZeroHomeHoursArray = _nzhHours.ToArray();
        }

        private static readonly string[] _monthNames =
        [
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        ];

        private async Task LoadSettingsAsync()
        {
            var list = await _settingsDataService!.GetList(set =>
                Task.FromResult(set.ToList()));

            _settings = list.FirstOrDefault() ?? new Settings();

            _chargingHours = _settings.ManualChargingHoursArray.ToList();
            _dischargingHours = _settings.ManualDischargingHoursArray.ToList();
            _nzhHours = _settings.ManualNetZeroHomeHoursArray.ToList();

            var stored = _settings.RequiredHomeEnergyArray;
            for (int i = 0; i < 12; i++)
                _energyNeeds[i] = i < stored.Length ? stored[i] : 0.0;

            StateHasChanged();
        }

        private async Task SaveSettingsAsync()
        {
            if (_settings == null) return;

            _settingsSaving = true;
            _settingsSaved = false;
            StateHasChanged();

            try
            {
                _settings.ManualChargingHoursArray = _chargingHours.ToArray();
                _settings.ManualDischargingHoursArray = _dischargingHours.ToArray();
                _settings.ManualNetZeroHomeHoursArray = _nzhHours.ToArray();
                _settings.RequiredHomeEnergyArray = _energyNeeds;

                if (_settings.Id == 0)
                {
                    await _settingsDataService!.Add(
                        [_settings],
                        (item, set) => set.Any());
                }
                else
                {
                    await _settingsDataService!.Update(
                        [_settings],
                        (item, set) => set.FirstOrDefault(s => s.Id == item.Id));
                }

                // Notify all services that settings have changed.
                if (_settingsService != null)
                    await _settingsService.RefreshAsync();

                _settingsSaved = true;
            }
            finally
            {
                _settingsSaving = false;
                StateHasChanged();
            }
        }
        // ── SQL Console ───────────────────────────────────────────────────────

        [Inject] private IServiceScopeFactory? _scopeFactory { get; set; }
        [Inject] private IJSRuntime? _js { get; set; }
        private string? _sqlStatement;
        private string? _sqlError;
        private string? _sqlRowsAffected;
        private bool _sqlBusy;
        private List<Dictionary<string, object>>? _sqlResult;
        private List<string> _sqlColumns = [];

        private static readonly HashSet<string> _blockedKeywords =
            new(StringComparer.OrdinalIgnoreCase) { "DROP", "ALTER", "CREATE", "TRUNCATE" };

        private async Task ExecuteSqlAsync()
        {
            if (string.IsNullOrWhiteSpace(_sqlStatement)) return;

            // Block destructive DDL statements.
            var upper = _sqlStatement.ToUpperInvariant();
            foreach (var kw in _blockedKeywords)
            {
                if (upper.Contains(kw))
                {
                    _sqlError = $"Statement contains blocked keyword: {kw}";
                    _sqlResult = null;
                    return;
                }
            }

            _sqlBusy = true;
            _sqlError = null;
            _sqlResult = null;
            _sqlRowsAffected = null;
            StateHasChanged();

            try
            {
                using var scope = _scopeFactory!.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ModelContext>();

                var isSelect = upper.TrimStart().StartsWith("SELECT");

                if (isSelect)
                {
                    // Use raw ADO.NET for SELECT — EF Core ExecuteSqlRaw does not return result sets.
                    var conn = db.Database.GetDbConnection();
                    await conn.OpenAsync();
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = _sqlStatement;
                        using var reader = await cmd.ExecuteReaderAsync();
                        _sqlColumns = Enumerable.Range(0, reader.FieldCount)
                            .Select(i => reader.GetName(i))
                            .ToList();
                        var rows = new List<Dictionary<string, object>>();
                        while (await reader.ReadAsync())
                        {
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < reader.FieldCount; i++)
                                row[reader.GetName(i)] = reader.IsDBNull(i) ? (object)"NULL" : reader.GetValue(i);
                            rows.Add(row);
                        }
                        _sqlResult = rows;
                        _sqlRowsAffected = $"{rows.Count} row(s) returned.";
                    }
                    finally
                    {
                        await conn.CloseAsync();
                    }
                }
                else
                {
                    // Use EF Core for UPDATE/DELETE — ensures correct connection and WAL checkpoint.
                    var affected = await db.Database.ExecuteSqlRawAsync(_sqlStatement);
                    _sqlResult = [];
                    _sqlRowsAffected = $"{affected} row(s) affected.";
                }
            }
            catch (Exception ex)
            {
                _sqlError = ex.Message;
            }
            finally
            {
                _sqlBusy = false;
                StateHasChanged();
            }
        }

        private string GetExportDirectory()
        {
            var dir = _settings?.ExportDirectory;
            if (string.IsNullOrWhiteSpace(dir)) dir = "/data/exports";
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private async Task ExportCsvAsync()
        {
            if (_sqlResult == null || _sqlColumns.Count == 0) return;
            try
            {
                // Build CSV content.
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Join(",", _sqlColumns.Select(c => "\"" + c + "\"")));
                foreach (var row in _sqlResult)
                {
                    var values = _sqlColumns.Select(c =>
                    {
                        var v = row.TryGetValue(c, out var val) ? val?.ToString() ?? string.Empty : string.Empty;
                        return "\"" + v.Replace("\"", "\"\"") + "\"";
                    });
                    sb.AppendLine(string.Join(",", values));
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                using var stream = new System.IO.MemoryStream(bytes);
                using var streamRef = new DotNetStreamReference(stream);
                await _js!.InvokeVoidAsync("downloadFileFromStream", "query_export.csv", streamRef);
            }
            catch (Exception ex)
            {
                _sqlError = $"CSV export failed: {ex.Message}";
                StateHasChanged();
            }
        }

        private async Task ExportXlsxAsync()
        {
            if (_sqlResult == null || _sqlColumns.Count == 0) return;
            try
            {
                using var ms = new System.IO.MemoryStream();

                using (var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(ms, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
                {
                    var workbookPart = doc.AddWorkbookPart();
                    workbookPart.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();

                    var worksheetPart = workbookPart.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
                    var sheetData = new DocumentFormat.OpenXml.Spreadsheet.SheetData();
                    worksheetPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(sheetData);

                    var sheets = workbookPart.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets());
                    sheets.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                    {
                        Id = workbookPart.GetIdOfPart(worksheetPart),
                        SheetId = 1,
                        Name = "Query"
                    });

                    static DocumentFormat.OpenXml.Spreadsheet.Cell CreateCell(string text) =>
                        new DocumentFormat.OpenXml.Spreadsheet.Cell
                        {
                            DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString,
                            InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(
                                new DocumentFormat.OpenXml.Spreadsheet.Text(text))
                        };

                    // Header row.
                    var headerRow = new DocumentFormat.OpenXml.Spreadsheet.Row();
                    foreach (var col in _sqlColumns)
                        headerRow.Append(CreateCell(col));
                    sheetData.Append(headerRow);

                    // Data rows.
                    foreach (var row in _sqlResult)
                    {
                        var dataRow = new DocumentFormat.OpenXml.Spreadsheet.Row();
                        foreach (var col in _sqlColumns)
                        {
                            var v = row.TryGetValue(col, out var val) ? val?.ToString() ?? string.Empty : string.Empty;
                            dataRow.Append(CreateCell(v));
                        }
                        sheetData.Append(dataRow);
                    }

                    workbookPart.Workbook.Save();
                }

                ms.Position = 0;
                using var streamRef = new DotNetStreamReference(ms);
                await _js!.InvokeVoidAsync("downloadFileFromStream", "query_export.xlsx", streamRef);
            }
            catch (Exception ex)
            {
                _sqlError = $"Excel export failed: {ex.Message}";
                StateHasChanged();
            }
        }
    }
}
using BlazorPro.BlazorSize;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.Json;
using Radzen;
using Radzen.Blazor;
using SessyCommon;
using SessyCommon.Configurations;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Interfaces;
using SessyController.Managers;
using SessyController.Providers;
using SessyController.Services;
using SessyController.Services.InverterServices;
using SessyController.Services.Items;
using SessyController.Services.StateMachine;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Controllers;

AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
{
    var senderType = sender?.GetType() ?? null;
    var ex = (Exception)eventArgs.ExceptionObject;

    Console.WriteLine($"🚨 Critical unhandled exception occurred: {ex.ToDetailedString()}");
    Console.WriteLine($"Sender is: {senderType?.FullName} IsTerminating: {eventArgs.IsTerminating}");
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ModelContext>();

builder.Logging.ClearProviders(); // Verwijder alle standaard logging providers
builder.Logging.AddConsole(); // Voeg alleen de console logger toe
builder.Logging.AddDebug(); // Voeg debug logging toe (optioneel)

string configDirectory = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? Directory.GetCurrentDirectory();

Console.WriteLine($"Configuratiemap: {configDirectory}");

if (!Directory.Exists(configDirectory))
    Console.WriteLine($"Config directory does not exist: {configDirectory}");

string appSettingsPath = Path.Combine(configDirectory, "appsettings.json");

if (File.Exists(appSettingsPath))
{
    // CreateBuilder already loaded the appsettings.json next to the binary. Configuration merges
    // per key instead of replacing whole sections, so the template batteries "2" and "3" in that
    // file survive a config that defines only "1" — the app then polls hardware that is not there
    // and dies on "Could not get power status ... for battery 2". The file under CONFIG_PATH is
    // the only source of truth; drop the built-in one. Environment overlays
    // (appsettings.Development.json) stay, they are not shipped as configuration templates.
    RemoveBuiltInAppSettings(builder.Configuration);

    builder.Configuration.AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true);
}
else
{
    Console.WriteLine("⚠️ Warning: appsettings.json missing!");
}

static void RemoveBuiltInAppSettings(IConfigurationBuilder configuration)
{
    for (var i = configuration.Sources.Count - 1; i >= 0; i--)
    {
        if (configuration.Sources[i] is JsonConfigurationSource json &&
            string.Equals(Path.GetFileName(json.Path), "appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Ignoring the built-in {json.Path}; the file under CONFIG_PATH is authoritative.");

            configuration.Sources.RemoveAt(i);
        }
    }
}

string secretsPath = Path.Combine(configDirectory, "secrets.json");

if (File.Exists(secretsPath))
{
    builder.Configuration.AddJsonFile(secretsPath, optional: false, reloadOnChange: true);
}
else
{
    Console.WriteLine("⚠️ Warning: secrets.json missing, secrets are not loaded.");
}

builder.Services.AddDbContext<ModelContext>(options =>
{
    options.UseSqlite(builder.Configuration.GetConnectionString("SQLiteConnection"));
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Voeg omgevingsvariabelen toe (voor Synology NAS en Docker)
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<SessyBatteryConfig>(builder.Configuration.GetSection("Sessy:Batteries"));
builder.Services.Configure<SessyP1Config>(builder.Configuration.GetSection("Sessy:Meters"));
builder.Services.Configure<PowerSystemsConfig>(builder.Configuration.GetSection("PowerSystems"));
builder.Services.Configure<SettingsConfig>(builder.Configuration.GetSection("ManagementSettings"));
builder.Services.Configure<WeatherExpectancyConfig>(builder.Configuration.GetSection("WeerOnline"));
builder.Services.Configure<SolarEdgeCloudConfig>(builder.Configuration.GetSection("SolarEdgeCloud"));
builder.Services.Configure<HeatPumpConfig>(builder.Configuration.GetSection("HeatPumpConfig"));

// Voeg services en providers toe aan de DI-container
builder.Services.AddHttpClient();

builder.Services.AddTransient(typeof(LoggingService<>));
builder.Services.AddTransient<Battery>();

// What this installation has (solar, …) so the UI can leave out what does not apply.
builder.Services.AddSingleton<SystemCapabilitiesService>();

builder.Services.AddScoped<ConfigurationService>();
builder.Services.AddScoped<SessyService>();
builder.Services.AddScoped<SolarService>();
builder.Services.AddScoped<TcpClientProvider>();
builder.Services.AddScoped<SessyStatusHistoryService>();
builder.Services.AddScoped<DbHelper>();
builder.Services.AddScoped<FinancialResultsService>();
builder.Services.AddSingleton<ConsumptionDataService>();
builder.Services.AddSingleton<InvestmentDataService>();
builder.Services.AddSingleton<InvestmentGroupDataService>();
// The one place a measured quarter is assembled — every reader of measured energy goes through it.
builder.Services.AddScoped<QuarterlyFactsService>();
builder.Services.AddScoped<EnergyStatisticsService>();
builder.Services.AddSingleton<ThrottleAnalysisService>();
builder.Services.AddSingleton<BatteryEfficiencyService>();
builder.Services.AddSingleton<ReplacementCostService>();
builder.Services.AddSingleton<PlannerLearningService>();
builder.Services.AddSingleton<ForecastSnapshotDataService>();

builder.Services.AddSingleton<QuarterlyMeasurementDataService>();
builder.Services.AddSingleton<InverterMeasurementDataService>();
builder.Services.AddSingleton<ICalculationService, CalculationService>();
builder.Services.AddSingleton<CalculationService>(sp => (CalculationService)sp.GetRequiredService<ICalculationService>());
builder.Services.AddSingleton<ChargeCostBasisService>();
builder.Services.AddSingleton<EnergyHistoryDataService>();
builder.Services.AddSingleton<SolarEdgeInverterService>();
builder.Services.AddSingleton<P1MeterService>();
builder.Services.AddSingleton<BatteryContainer>();
builder.Services.AddSingleton<IBatteryContainer>(sp => sp.GetRequiredService<BatteryContainer>());
builder.Services.AddSingleton<TimeZoneService>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<EPEXPricesService>();
builder.Services.AddSingleton<IEPEXPricesService>(sp => sp.GetRequiredService<EPEXPricesService>());
builder.Services.AddSingleton<ProfitMaximizationMilpService>();
builder.Services.AddSingleton<SelfConsumptionMilpService>();
builder.Services.AddSingleton<BalancedMilpService>();
builder.Services.AddSingleton<BatterySavingMilpService>();
builder.Services.AddSingleton<IMilpService, MilpServiceProxy>();
builder.Services.AddSingleton<ConfigurationCheckService>();
// ── State machine ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<HardwareStatusService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareStatusService>());
builder.Services.AddSingleton<EnergySystemStateMachine>();
// ─────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BatteriesService>();
builder.Services.AddSingleton<GridTargetService>();
builder.Services.AddSingleton<SessyMonitorService>();
builder.Services.AddSingleton<EnergyMonitorService>();
builder.Services.AddSingleton<SolarDataService>();
builder.Services.AddSingleton<EPEXPricesDataService>();
builder.Services.AddSingleton<IGasPricesDataService, GasPricesDataService>();
builder.Services.AddSingleton<GasPricesDataService>(sp => (GasPricesDataService)sp.GetRequiredService<IGasPricesDataService>());
builder.Services.AddSingleton<PlannedActionDataService>();
builder.Services.AddSingleton<PlannedQuarterDataService>();
builder.Services.AddSingleton<ActualQuarterDataService>();
builder.Services.AddSingleton<PlanVsActualService>();
builder.Services.AddSingleton<SettingsDataService>();
builder.Services.AddSingleton<SettingsService>();

// Single source of truth for who drives the batteries — read by SessyService's write guards and
// by the UI, updated once per cycle by BatteriesService.
builder.Services.AddSingleton<ControlModeService>();

builder.Services.AddSingleton<ExpectedPriceService>();
builder.Services.AddSingleton<SessyWebControlDataService>();
builder.Services.AddSingleton<AppVersionDataService>();
builder.Services.AddSingleton<ChargedScheduleService>();
builder.Services.AddSingleton<TaxesDataService>();
builder.Services.AddSingleton<ConsumptionMonitorService>();
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddSingleton<DatabaseBackupDataService>();
builder.Services.AddSingleton<P1MeterContainer>();

// Solar inverters
builder.Services.AddSingleton<SolarInverterManager>();
builder.Services.AddSingleton<InverterCurtailmentService>();

// For now only the SolarEdge inverter is implemented (for obvious reasons :-), I don't have the other inverters.
builder.Services.AddSingleton<ISolarInverterService, SolarEdgeInverterService>();
// These are not implemented yet, but the interfaces are there for future use.
builder.Services.AddSingleton<ISolarInverterService, EnphaseInverterService>();
builder.Services.AddSingleton<ISolarInverterService, GoodWeInverterService>();
builder.Services.AddSingleton<ISolarInverterService, HuaweiInverterService>();
builder.Services.AddSingleton<ISolarInverterService, SungrowInverterService>();
builder.Services.AddSingleton<ISolarInverterService, VictronInverterService>();
// Reads PV from the Sessy batteries themselves — for households whose inverter cannot be read.
builder.Services.AddSingleton<SessyInverterService>();
builder.Services.AddSingleton<ISolarInverterService>(provider => provider.GetRequiredService<SessyInverterService>());

// SettingsService must start first — all other background services depend on Settings.Current.
builder.Services.AddHostedService(provider => provider.GetRequiredService<SettingsService>());

builder.Services.AddHostedService(provider => provider.GetRequiredService<EPEXPricesService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<BatteriesService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<GridTargetService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<WeatherService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SessyMonitorService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<EnergyMonitorService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<P1MeterService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SolarInverterManager>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<ConsumptionMonitorService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<DatabaseBackupService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<InverterCurtailmentService>());

// Reports thread-pool starvation — the mechanism behind a UI that stalls on every page at once.
builder.Services.AddSingleton<ThreadPoolMonitorService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ThreadPoolMonitorService>());

// (was AddScoped, nu AddHostedService omdat het een BackgroundService is)
builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new IgnoreAntiforgeryTokenAttribute());
});

builder.Services.AddServerSideBlazor(options =>
{
    // Keep disconnected circuits alive long enough to survive a VPN reconnect on iPhone
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(5);
    options.DisconnectedCircuitMaxRetained = 100;
})
    .AddHubOptions(options =>
    {
        // Give the client more time before the server drops the connection.
        // Rule: ClientTimeoutInterval must be >= 2x KeepAliveInterval
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });

builder.Services.AddScoped<Radzen.DialogService>();
builder.Services.AddScoped<Radzen.NotificationService>();
builder.Services.AddScoped<Radzen.TooltipService>();
builder.Services.AddScoped<Radzen.ContextMenuService>();
builder.Services.AddScoped<Radzen.ThemeService>();
builder.Services.AddScoped<RadzenTheme>();
builder.Services.AddHttpContextAccessor();

// For swagger. Use https://<baseurl>/swagger in a browser to see this page.
builder.Services.AddScoped<BatteryManagementController>();

builder.Services.AddControllers();
builder.Services.AddScoped<SessyController.Services.DataEditorService>();
builder.Services.AddRadzenComponents();

// Remove the antiforgery token.
builder.Services.AddRadzenCookieThemeService(options =>
{
    options.Name = "SessyTheme"; // The name of the cookie
    options.Duration = TimeSpan.FromDays(365); // The duration of the cookie
});

// Globale exception handler voor logging
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddDebug();
});

// Add global error-handling middleware
builder.Services.AddSingleton<IStartupFilter, GlobalExceptionHandlingStartupFilter>();

// This code prevents a null reference exception in RadzenThemeDispose() for now but according to
// Radzen support this should not be needed. In a future version of Radzen this problem is
// solved (see: https://forum.radzen.com/t/radzentheme-dispose-null-reference-exception/19661/4).
builder.Services.AddSingleton<RadzenTheme>(provider =>
{
    var theme = new RadzenTheme();
    return theme;
});

builder.Services.AddResizeListener();

var app = builder.Build();

DockerService.IsRunningInDocker(true);

ServiceLocator.ServiceProvider = app.Services;

// Wire TimeZoneService to update its timezone when settings change in the database.
app.Services.GetRequiredService<SettingsService>().SettingsChanged += (s, _) =>
    app.Services.GetRequiredService<TimeZoneService>().UpdateTimezone(s.TimeZone);

Console.WriteLine("Migrating database (if needed)");

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var dbContext = services.GetRequiredService<ModelContext>();

    // Once per database, before anything else opens it: under WAL readers no longer queue behind
    // writers, which is what made the UI stall on every page. Reports and continues on failure.
    SqliteSetup.EnableWriteAheadLogging(dbContext.Database.GetDbConnection());

    // The timezone lives in the Settings row, but SettingsService only loads it once the hosted
    // services run — after this block. Both the pre-migration backup and the AppVersions stamp
    // below are timestamped, so without this they would use the default while the rest of the
    // application uses the configured zone. Read it straight from the file; a database without a
    // Settings row simply keeps the default.
    var storedTimeZone = SqliteSetup.TryReadTimeZone(dbContext.Database.GetDbConnection());

    if (!string.IsNullOrWhiteSpace(storedTimeZone))
    {
        services.GetRequiredService<TimeZoneService>().UpdateTimezone(storedTimeZone);

        Console.WriteLine($"Timezone from the database: {storedTimeZone}");
    }

    var pendingMigrations = dbContext.Database.GetPendingMigrations();

    if (pendingMigrations.Any())
    {
        Console.WriteLine("Database has pending model changes, backing up database...");

        var databaseScope = scope.ServiceProvider.GetRequiredService<DbHelper>();

        databaseScope.BackupDatabase().GetAwaiter().GetResult();
    }

    dbContext.Database.Migrate();

    // Stamp the build into the database, so a database file always says which versions have run
    // against it — a backup or a copy pulled off the NAS is otherwise anonymous.
    var appVersionDataService = services.GetRequiredService<AppVersionDataService>();
    var lastMigration = dbContext.Database.GetAppliedMigrations().LastOrDefault() ?? string.Empty;

    var previousVersion = appVersionDataService
        .RecordStartupAsync(AppInfo.Version, lastMigration, services.GetRequiredService<TimeZoneService>().Now)
        .GetAwaiter().GetResult();

    if (previousVersion != null && previousVersion.Version != AppInfo.Version)
        Console.WriteLine($"Database last ran under {previousVersion.Version}, now {AppInfo.Version}");
}

Console.WriteLine($"Database Migration complete (SessyWeb {AppInfo.Version})");

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        if (exceptionHandlerPathFeature?.Error != null)
        {
            Console.WriteLine($"An unexpected exception occurred\n\n{exceptionHandlerPathFeature.Error.ToDetailedString()}");
        }

        context.Response.Redirect("/error");

        return Task.CompletedTask;
    });
});

Console.WriteLine("Swagger available");

app.UseSwagger();
app.UseSwaggerUI();

if (app.Environment.IsDevelopment())
{
    Console.WriteLine("Development environment");
}
else
{
    Console.WriteLine("Production environment");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();

Console.WriteLine("Sessy web is starting....");

app.Run();
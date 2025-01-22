using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using SessyController.Configurations;
using SessyController.Extensions;
using SessyController.Providers;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Controllers;
using SessyWeb.Data;


var builder = WebApplication.CreateBuilder(args);

string configDirectory = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? Directory.GetCurrentDirectory();


Console.WriteLine($"Configuratiemap: {configDirectory}");

if (!Directory.Exists(configDirectory))
    Console.WriteLine($"Config directory does not exist: {configDirectory}");

// 🔹 2. Laad appsettings.json vanuit de opgegeven directory
string appSettingsPath = Path.Combine(configDirectory, "appsettings.json");
if (File.Exists(appSettingsPath))
{
    builder.Configuration.AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true);
}
else
{
    Console.WriteLine("⚠️ Waarschuwing: appsettings.json ontbreekt!");
}

// 🔹 3. Laad secrets.json als het aanwezig is
string secretsPath = Path.Combine(configDirectory, "secrets.json");
if (File.Exists(secretsPath))
{
    builder.Configuration.AddJsonFile(secretsPath, optional: true, reloadOnChange: true);
}
else
{
    Console.WriteLine("⚠️ Waarschuwing: secrets.json ontbreekt, geheimen worden niet geladen.");
}

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Voeg omgevingsvariabelen toe (voor Synology NAS en Docker)
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<SessyBatteryConfig>(builder.Configuration.GetSection("Sessy:Batteries"));
builder.Services.Configure<SessyP1Config>(builder.Configuration.GetSection("Sessy:Meters"));
builder.Services.Configure<ModbusConfig>(builder.Configuration.GetSection("PowerSystems"));
builder.Services.Configure<SettingsConfig>(builder.Configuration.GetSection("ManagementSettings"));
builder.Services.Configure<SunExpectancyConfig>(builder.Configuration.GetSection("WeerOnline"));

// Voeg services en providers toe aan de DI-container
builder.Services.AddHttpClient();

builder.Services.AddTransient(typeof(LoggingService<>));
builder.Services.AddScoped<SessyService>();
builder.Services.AddScoped<SolarEdgeService>();
builder.Services.AddScoped<P1MeterService>();
builder.Services.AddScoped<SunExpectancyService>();
builder.Services.AddSingleton<DayAheadMarketService>();
builder.Services.AddSingleton<BatteriesService>();
builder.Services.AddScoped<TcpClientProvider>();
builder.Services.AddTransient<Battery>();
builder.Services.AddScoped<BatteryContainer>();

builder.Services.AddHostedService(provider => provider.GetRequiredService<DayAheadMarketService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<BatteriesService>());

// Add services to the container.

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();

builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();

builder.Services.AddScoped<BatteryManagementController>();
builder.Services.AddControllers();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    Console.WriteLine("Development environment");

#if SWAGGER
    app.UseSwagger();
    app.UseSwaggerUI();
#endif
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

app.Run();

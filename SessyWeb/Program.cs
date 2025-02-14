﻿using Microsoft.EntityFrameworkCore;
using Radzen;
using Radzen.Blazor;
using SessyCommon.Extensions;
using SessyController.Configurations;
using SessyController.Providers;
using SessyController.Services;
using SessyController.Services.Items;
using SessyData.Helpers;
using SessyData.Model;
using SessyData.Services;
using SessyWeb.Controllers;
using SessyWeb.Services;

AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
{
    var ex = (Exception)eventArgs.ExceptionObject;
    Console.WriteLine($"🚨 Critical unhandled exception occurred: {ex.ToDetailedString()}");
};

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders(); // Verwijder alle standaard logging providers
builder.Logging.AddConsole(); // Voeg alleen de console logger toe
builder.Logging.AddDebug(); // Voeg debug logging toe (optioneel)

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

builder.Services.AddDbContext<ModelContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("SQLiteConnection")));

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

// Voeg services en providers toe aan de DI-container
builder.Services.AddHttpClient();

builder.Services.AddTransient(typeof(LoggingService<>));
builder.Services.AddTransient<Battery>();

builder.Services.AddScoped<SessyService>();
builder.Services.AddScoped<SolarEdgeService>();
builder.Services.AddScoped<SolarService>();
builder.Services.AddScoped<P1MeterService>();
builder.Services.AddScoped<TcpClientProvider>();
builder.Services.AddScoped<BatteryContainer>();
builder.Services.AddScoped<SolarHistoryService>();
builder.Services.AddScoped<SessyStatusHistoryService>();
builder.Services.AddScoped<DbHelper>();

builder.Services.AddSingleton<TimeZoneService>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<DayAheadMarketService>();
builder.Services.AddSingleton<BatteriesService>();
builder.Services.AddSingleton<SessyMonitorService>();
builder.Services.AddScoped<ScreenSizeService>();

builder.Services.AddHostedService(provider => provider.GetRequiredService<DayAheadMarketService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<BatteriesService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<WeatherService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SessyMonitorService>());

// Add services to the container.

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

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
builder.Services.AddRadzenComponents();

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

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var dbContext = services.GetRequiredService<ModelContext>();

    dbContext.Database.Migrate();
}


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

if (app.Environment.IsDevelopment())
{
    Console.WriteLine("Development environment");

    app.UseSwagger();
    app.UseSwaggerUI();
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

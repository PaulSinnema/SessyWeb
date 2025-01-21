using Microsoft.AspNetCore.Diagnostics;
using SessyController.Configurations;
using SessyController.Extensions;
using SessyController.Providers;
using SessyController.Services;
using SessyController.Services.Items;

var builder = WebApplication.CreateBuilder(args);

// 🔹 1. Haal de configuratie directory op uit de PATH omgevingsvariabele
string configDirectory = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? Directory.GetCurrentDirectory();


Console.WriteLine($"Configuratiemap: {configDirectory}");

if(!Path.Exists(configDirectory))
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

// 🔹 4. Voeg omgevingsvariabelen toe (voor Synology NAS en Docker)
builder.Configuration.AddEnvironmentVariables();

// Add services to the container.
builder.Services.AddControllers();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 🔹 5. Bind Sessy configuratie-instellingen
builder.Services.Configure<SessyBatteryConfig>(builder.Configuration.GetSection("Sessy:Batteries"));
builder.Services.Configure<SessyP1Config>(builder.Configuration.GetSection("Sessy:Meters"));
builder.Services.Configure<ModbusConfig>(builder.Configuration.GetSection("PowerSystems"));
builder.Services.Configure<SettingsConfig>(builder.Configuration.GetSection("ManagementSettings"));
builder.Services.Configure<SunExpectancyConfig>(builder.Configuration.GetSection("WeerOnline"));

// 🔹 6. Voeg services en providers toe aan de DI-container
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    Console.WriteLine("Development environment");

    app.UseSwagger();
    app.UseSwaggerUI();
}
else if(app.Environment.IsProduction())
{
    Console.WriteLine("Production environment, adding Swagger");

    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = exceptionHandlerPathFeature?.Error;

        // Log de fout
        var detailedException = exception.ToDetailedString();
        Console.WriteLine(detailedException);
        context.Response.StatusCode = 500;

        if (app.Environment.IsDevelopment())
            await context.Response.WriteAsync($"Internal Server Error\n\n{detailedException}");
        else
            await context.Response.WriteAsync($"Internal Server Error");
    });
});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

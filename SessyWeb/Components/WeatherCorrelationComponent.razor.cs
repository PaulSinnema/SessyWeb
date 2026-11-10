using Microsoft.AspNetCore.Components;
using SessyController.Services.Statistics;

namespace SessyWeb.Components
{
    /// <summary>
    /// Daily house consumption against temperature, global radiation and humidity: one scatter per
    /// weather variable, coloured per season, with the overall least-squares line and its r / R².
    /// </summary>
    public partial class WeatherCorrelationComponent : BaseComponent
    {
        [Inject]
        private WeatherCorrelationService? WeatherCorrelationService { get; set; }

        /// <summary>One point of a scatter; X is numeric, so Radzen draws a linear category axis.</summary>
        public sealed record Point(double X, double Y);

        public sealed record SeasonSeries(Season Season, string Color, List<Point> Points);

        public sealed record Panel(string Title, string Unit, string Info, List<SeasonSeries> Seasons, List<Point> Trend, LinearFit? Fit);

        private const string HowToRead =
            "How to read these charts: every dot is one day. Left to right is the weather that day, bottom to top is " +
            "how much electricity the house used that day (kWh). The colour is the season. The dashed line is the " +
            "average trend through all dots: the steeper it runs, the more your consumption follows the weather. " +
            "R² tells how much of the difference between days this one weather factor explains: 0.34 means 34%; " +
            "the rest comes from everything else (washing, cooking, who is home).";

        private const string TemperatureInfo =
            "Consumption against the day's average outside temperature. Cold days on the left usually sit high: " +
            "heating takes a lot of electricity. If the dots rise again on the far right, warm days cost extra too " +
            "(cooling). The slope below the title says how many kWh a day one degree warmer saves (negative) on average.";

        private const string RadiationInfo =
            "Consumption against the day's average sunshine (global radiation, W/m²): left is grey, right is sunny. " +
            "Sunny days are usually also warmer, so part of what you see here is really the temperature effect. " +
            "Solar panels do not show here: this is what the house used, not what came from the grid.";

        private const string HumidityInfo =
            "Consumption against the day's average relative humidity (%). Damp days are usually also cold and grey, " +
            "so a rising line here mostly reflects winter. Humidity on its own rarely drives consumption much; a low " +
            "R² is the expected outcome.";

        private static readonly (Season Season, string Color)[] SeasonColors =
        [
            (Season.Winter, "var(--sessy-sell-line)"),
            (Season.Spring, "var(--sessy-discharge-line)"),
            (Season.Summer, "var(--sessy-solar-line)"),
            (Season.Autumn, "var(--sessy-charge-line)")
        ];

        private bool _loading = true;
        private int _dayCount;
        private DateTime? _firstDay;
        private DateTime? _lastDay;
        private List<Panel> _panels = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (WeatherCorrelationService == null) return;

            _loading = true;

            try
            {
                // Off the circuit thread: all quarters since the start of recording.
                var days = await Task.Run(() => WeatherCorrelationService.GetDaysAsync()).ConfigureAwait(true);

                _dayCount = days.Count;
                _firstDay = days.FirstOrDefault()?.Date;
                _lastDay = days.LastOrDefault()?.Date;
                _panels =
                [
                    BuildPanel("Temperature", "°C", TemperatureInfo, days, d => d.TemperatureC),
                    BuildPanel("Global radiation", "W/m²", RadiationInfo, days, d => d.GlobalRadiation),
                    BuildPanel("Humidity", "%", HumidityInfo, days, d => d.Humidity)
                ];
            }
            finally
            {
                _loading = false;
            }
        }

        private static Panel BuildPanel(string title, string unit, string info, List<WeatherDay> days, Func<WeatherDay, double> x)
        {
            var seasons = SeasonColors
                .Select(sc => new SeasonSeries(sc.Season, sc.Color,
                    days.Where(d => d.Season == sc.Season)
                        .Select(d => new Point(x(d), d.ConsumptionKWh))
                        .OrderBy(p => p.X)
                        .ToList()))
                .Where(s => s.Points.Count > 0)
                .ToList();

            var fit = WeatherCorrelationService.Fit(days.Select(d => (x(d), d.ConsumptionKWh)).ToList());

            var trend = new List<Point>();
            if (fit != null)
            {
                double minX = days.Min(x);
                double maxX = days.Max(x);
                trend.Add(new Point(minX, fit.At(minX)));
                trend.Add(new Point(maxX, fit.At(maxX)));
            }

            return new Panel(title, unit, info, seasons, trend, fit);
        }

        private string FitText(Panel panel) => panel.Fit == null
            ? "Not enough data"
            : $"r = {panel.Fit.R.ToString("F2", GetFormatProvider())}, " +
              $"R² = {panel.Fit.RSquared.ToString("F2", GetFormatProvider())} " +
              $"({(panel.Fit.RSquared * 100).ToString("F0", GetFormatProvider())}% of the day-to-day variation), " +
              $"{panel.Fit.Slope.ToString("F2", GetFormatProvider())} kWh per {panel.Unit}, {panel.Fit.N} days";
    }
}

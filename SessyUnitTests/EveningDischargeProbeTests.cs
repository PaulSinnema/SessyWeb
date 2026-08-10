using Microsoft.Data.Sqlite;
using SessyController.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Diagnostic harness: runs the real BatteryGreedyPlanner on the real inputs of a plan that is
    /// already in the production database, so a plan that looks wrong can be taken apart input by
    /// input instead of reasoned about.
    ///
    /// Why it exists: on 10-08-2026 the plan sold only four quarters of the following evening and
    /// kept 9.4 kWh, while every threshold computed by hand said it should sell roughly nine. Every
    /// static explanation (reserve, replacement-cost floor, iteration cap, discharge capability)
    /// was eliminated on paper, which meant the reasoning — not the planner — was wrong somewhere.
    ///
    /// Skips itself when the database is not there, so it is harmless on any other machine.
    /// </summary>
    public class EveningDischargeProbeTests
    {
        private const string DatabasePath = @"C:\Projects\Sessy\SessyWeb\SessyController\Data\Sessy.db";

        /// <summary>
        /// The quarter the plan under investigation started from. Taken from the oldest row that
        /// carries the planner's own inputs, so the probe follows the newest plan automatically.
        /// </summary>
        private static DateTime HorizonStart { get; set; } = new(2026, 8, 10, 16, 15, 0);

        private static void SetHorizonToNewestPlan()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "select min(Time) from PlannedQuarters where MinSocWh > 0";

            if (command.ExecuteScalar() is string start)
                HorizonStart = DateTime.Parse(start);
        }

        // Measured on the production database, 31 days — see the session notes.
        private const double CapacityKWh = 16.2;
        private const double MaxChargeKW = 6.6;
        private const double MaxDischargeKW = 5.1;
        private const double CycleCostEurPerKWh = 0.0862;
        private const double ReplacementCostEurPerKWh = 0.1291;
        private const double DiscountPerHour = 0.003;
        private const double NightReserveCapRatio = 0.32788;
        private const double ReserveSafetyFactor = 1.1;

        private readonly ITestOutputHelper _output;

        public EveningDischargeProbeTests(ITestOutputHelper output) => _output = output;

        private sealed record Quarter(
            DateTime Time, string Mode, double PlanW, double LeftWh,
            double Buy, double Sell, double SolarW, double ConsW,
            double StoredNetLoadWh, double StoredMinSocWh)
        {
            /// <summary>
            /// Household load minus solar, in Wh for the quarter — QuarterlyInfo.NetLoadWh.
            ///
            /// The two stored columns are NOT in the same unit, whatever their names say.
            /// ConsumptionForecastW really is Watts, so it needs the quarter of an hour.
            /// SolarForecastW is written from SolarPowerPerQuarterInWatts, which is
            /// SolarPowerPerQuarterHour × 1000 — already Wh for the quarter. Multiplying that by
            /// 0.25 as well gives the planner a quarter of the sun there really is.
            /// </summary>
            public double NetLoadWh => StoredNetLoadWh != 0.0 ? StoredNetLoadWh : ConsW * 0.25 - SolarW;
            public double SolarSurplusWh => Math.Max(0.0, -NetLoadWh);
        }

        private static List<Quarter> ReadPlan()
        {
            var rows = new List<Quarter>();

            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            // NetLoadWh and MinSocWh only exist from v1.0.89 on; older rows report 0 and the
            // reconstruction below fills in.
            bool hasPlannerInputs = HasColumn(connection, "PlannedQuarters", "MinSocWh");

            command.CommandText =
                @"select Time, PlannedMode, PlannedPowerW, PlannedChargeLeftWh,
                         BuyingPriceEurKWh, SellingPriceEurKWh, SolarForecastW, ConsumptionForecastW"
                + (hasPlannerInputs ? ", NetLoadWh, MinSocWh" : ", 0.0, 0.0") +
                @" from PlannedQuarters
                  where Time >= $start
                  order by Time";
            command.Parameters.AddWithValue("$start", HorizonStart.ToString("yyyy-MM-dd HH:mm:ss"));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Quarter(
                    DateTime.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8),
                    reader.GetDouble(9)));
            }

            return rows;
        }

        /// <summary>
        /// The charge floor as ThrottleAnalysisService would fit it, read straight from the
        /// measurements — same query shape as CollectChargeSamplesAsync.
        /// </summary>
        private ChargeCapabilityFloor MeasuredChargeFloor(double nameplateW, double capacityWh)
        {
            var samples = new List<(double Soc, double PowerW)>();

            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                @"select BatteryStateOfChargeWh, abs(BatteryPowerWatts)
                  from QuarterlyMeasurements
                  where BatteryMode = 1 and IsReliable = 1";   // 1 = Modes.Charging

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                double soc = reader.GetDouble(0) / capacityWh;
                if (soc < 0.0 || soc > 1.0) continue;

                samples.Add((soc, reader.GetDouble(1)));
            }

            var floor = ThrottleAnalysisService.FitChargeCapabilityFloor(samples, nameplateW);

            _output.WriteLine(
                $"  charge floor from {samples.Count} charging quarters: " +
                $"{floor.PowerW(0.5):F0} W at 50% SOC, {floor.PowerW(0.8):F0} W at 80%, " +
                $"{floor.CoveredBins} bins covered");
            _output.WriteLine("");

            return floor;
        }

        private static bool HasColumn(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"select count(*) from pragma_table_info('{table}') where name = $column";
            command.Parameters.AddWithValue("$column", column);

            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        /// <summary>SOC read back from the quarter before the horizon, which is where the plan started.</summary>
        private static double InitialSocKWh()
        {
            using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                @"select PlannedChargeLeftWh from PlannedQuarters
                  where Time < $start order by Time desc limit 1";
            command.Parameters.AddWithValue("$start", HorizonStart.ToString("yyyy-MM-dd HH:mm:ss"));

            var value = command.ExecuteScalar();
            return value == null ? 0.0 : Convert.ToDouble(value) / 1000.0;
        }

        /// <summary>
        /// Mirrors MilpServiceBase.ComputeSocBounds: reserve what the house needs until the next
        /// solar window, minus what the sun refills before then, capped at a share of capacity.
        /// </summary>
        private static List<SocBound> BuildSocBounds(List<Quarter> quarters)
        {
            // Whatever the planner really used beats any reconstruction of it.
            if (quarters.Any(q => q.StoredMinSocWh > 0.0))
                return quarters
                    .Select(q => new SocBound(q.Time, q.StoredMinSocWh / 1000.0, CapacityKWh))
                    .ToList();

            var bounds = new List<SocBound>();

            for (int i = 0; i < quarters.Count; i++)
            {
                double nightReserveWh = 0.0;
                double solarBeforeWh = 0.0;
                bool solarSeen = false;

                for (int j = i + 1; j < quarters.Count; j++)
                {
                    double netLoad = quarters[j].NetLoadWh;

                    if (netLoad <= 0.0)
                    {
                        if (solarSeen && nightReserveWh > 0.0) break;

                        solarSeen = true;
                        solarBeforeWh += -netLoad;
                        continue;
                    }

                    nightReserveWh += netLoad;
                }

                double neededWh = Math.Max(0.0, nightReserveWh - solarBeforeWh);
                double minSocWh = Math.Min(neededWh * ReserveSafetyFactor, CapacityKWh * 1000.0 * NightReserveCapRatio);

                bounds.Add(new SocBound(quarters[i].Time, minSocWh / 1000.0, CapacityKWh));
            }

            return bounds;
        }

        private static List<PricePoint> BuildPricePoints(List<Quarter> quarters) =>
            quarters.Select(q => new PricePoint(
                Start: q.Time,
                BuyEurPerKWh: q.Buy,
                SellEurPerKWh: q.Sell,
                NetLoadWh: q.NetLoadWh,
                SolarSurplusWh: q.SolarSurplusWh)).ToList();

        /// <summary>The fit reproduced from 31 days of QuarterlyMeasurements.</summary>
        private static EfficiencyCurve MeasuredCurve() =>
            new(ChargeCeiling: 0.8415, ChargeOverheadKW: 0.0580,
                DischargeCeiling: 0.9278, DischargeOverheadKW: 0.0645,
                Samples: 1667);

        private PlanResult Run(string label, SessyOptions options, BatterySpec spec,
                               List<PricePoint> points, List<SocBound> bounds, List<Quarter> quarters)
        {
            var result = BatteryGreedyPlanner.Solve(points, spec, options, bounds);
            Assert.NotNull(result);

            var evening = result!.Plan
                .Where(p => p.Start >= new DateTime(2026, 8, 11, 18, 0, 0))
                .ToList();

            double dischargedKWh = evening.Sum(p => p.DischargeKW * 0.25);
            int dischargeQuarters = evening.Count(p => p.DischargeKW > 0.01);
            double endSocKWh = result.Plan.Last().SocEndKWh;

            _output.WriteLine(
                $"{label,-34} objective € {result.ObjectiveEur,7:F4}   " +
                $"evening: {dischargeQuarters,2} quarters, {dischargedKWh,6:F2} kWh   " +
                $"end SOC {endSocKWh,6:F2} kWh");

            return result;
        }

        /// <summary>
        /// Replays the newest recorded solve input verbatim — no reconstruction at all. This is
        /// the one run where a difference with production can only come from the planner itself.
        /// Needs SESSY_RECORD_SOLVE_INPUTS set on the machine that built the plan.
        /// </summary>
        [Fact]
        public void Replay_the_recorded_solve_input()
        {
            // Settings.ExportDirectory is "/data/exports", which on Windows lands on the current
            // drive; the Data folder next to the database is the other place worth looking.
            string[] directories =
            [
                @"C:\data\exports",
                @"C:\Projects\Sessy\SessyWeb\SessyController\Data",
                @"C:\Projects\Sessy\SessyWeb\data\exports"
            ];

            var newest = directories
                .Where(Directory.Exists)
                .SelectMany(d => new DirectoryInfo(d).GetFiles("solve-input-*.json"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest == null)
            {
                _output.WriteLine(
                    $"No solve-input-*.json found in {string.Join(", ", directories)} — set " +
                    $"{SolveInputRecorder.EnableVariable}=1 and let the app build one plan.");
                return;
            }

            var input = SolveInputRecorder.Read(newest.FullName);
            Assert.NotNull(input);

            _output.WriteLine($"Replaying {newest.Name}, recorded {input!.RecordedAt:dd-MM HH:mm}");
            _output.WriteLine($"  quarters {input.PricePoints.Count}, initial SOC {input.Spec.InitialSocKWh:F2} kWh");
            _output.WriteLine($"  taper {(input.Spec.ChargeTaper?.Samples ?? 0)} samples, " +
                              $"efficiency {(input.Spec.Efficiency?.Samples ?? 0)} samples, " +
                              $"capability {(input.Spec.DischargeCapability?.Samples ?? 0)} samples");
            _output.WriteLine($"  options {input.Options}");
            _output.WriteLine("");

            void Replay(string label, IReadOnlyList<PricePoint> points, BatterySpec spec,
                        SessyOptions options, IReadOnlyList<SocBound> bounds)
            {
                var plan = BatteryGreedyPlanner.Solve(points, spec, options, bounds);
                Assert.NotNull(plan);

                var evening = plan!.Plan.Where(p => p.Start >= new DateTime(2026, 8, 11, 18, 0, 0)).ToList();

                // Only count real arbitrage quarters: the ZeroNetHome baseline also discharges,
                // at a few hundred watts, and counting those hides the difference.
                _output.WriteLine(
                    $"  {label,-30} € {plan.ObjectiveEur,7:F4}   evening " +
                    $"{evening.Count(p => p.DischargeKW > 1.0),2} quarters > 1 kW, " +
                    $"{evening.Sum(p => p.DischargeKW * 0.25),5:F2} kWh, " +
                    $"end SOC {plan.Plan.Last().SocEndKWh,5:F2} kWh");
            }

            var noCaps = input.PricePoints
                .Select(p => p with { MaxChargeKW = null, MaxDischargeKW = null })
                .ToList();

            // The fix under test: the same input, plus the floor measured on the charging quarters
            // in the production database. Everything else identical.
            var floor = MeasuredChargeFloor(input.Spec.MaxChargeKW * 1000.0, input.Spec.CapacityKWh * 1000.0);

            Replay("as recorded", input.PricePoints, input.Spec, input.Options, input.SocBounds);
            Replay("+ measured charge floor", input.PricePoints, input.Spec with { ChargeFloor = floor },
                   input.Options, input.SocBounds);
            Replay("no discharge capability", input.PricePoints, input.Spec with { DischargeCapability = null }, input.Options, input.SocBounds);
            Replay("no charge taper", input.PricePoints, input.Spec with { ChargeTaper = null }, input.Options, input.SocBounds);
            Replay("no per-quarter power caps", noCaps, input.Spec, input.Options, input.SocBounds);
            Replay("no reserve", input.PricePoints, input.Spec, input.Options,
                   input.SocBounds.Select(b => b with { MinSocKWh = 0.0 }).ToList());
            Replay("no replacement cost", input.PricePoints, input.Spec, input.Options with { ReplacementCostEurPerKWh = 0.0 }, input.SocBounds);
            Replay("no discount", input.PricePoints, input.Spec, input.Options with { FutureValueDiscountPerHour = 0.0 }, input.SocBounds);
            Replay("no cycle cost", input.PricePoints, input.Spec, input.Options with { CycleCostEurPerKWh = 0.0 }, input.SocBounds);
            Replay("flat efficiency", input.PricePoints, input.Spec with { Efficiency = null }, input.Options, input.SocBounds);

            int reserveOnly = input.PricePoints.Count(p => p.ReserveOnly);
            _output.WriteLine("");
            _output.WriteLine($"  reserve-only quarters in the input: {reserveOnly} of {input.PricePoints.Count}");

            // Verification step 3: with the floor in place, is anything still left on the table?
            // A quarter the planner itself scores as profitable and does not take would be a
            // second cause hiding behind the first.
            foreach (var (label, spec) in new[]
                     {
                         ("as recorded", input.Spec),
                         ("with floor", input.Spec with { ChargeFloor = floor })
                     })
            {
                var lines = new List<string>();
                BatteryGreedyPlanner.Solve(input.PricePoints, spec, input.Options, input.SocBounds, lines.Add);

                var profitable = lines.Where(l => l.Contains("PROFITABLE")).ToList();

                _output.WriteLine("");
                _output.WriteLine($"  {label}: {profitable.Count} quarters rejected while profitable");

                foreach (var line in profitable.Take(8))
                    _output.WriteLine($"    {line}");
            }
        }

        [Fact]
        public void Why_does_the_evening_stay_unsold()
        {
            if (!File.Exists(DatabasePath))
            {
                _output.WriteLine($"No database at {DatabasePath} — probe skipped.");
                return;
            }

            SetHorizonToNewestPlan();

            var quarters = ReadPlan();
            if (quarters.Count == 0)
            {
                _output.WriteLine("No planned quarters in the window — probe skipped.");
                return;
            }

            var points = BuildPricePoints(quarters);
            var bounds = BuildSocBounds(quarters);
            double initialSoc = InitialSocKWh();

            _output.WriteLine($"Quarters {quarters.Count}, {quarters[0].Time:dd-MM HH:mm} .. {quarters[^1].Time:dd-MM HH:mm}");
            _output.WriteLine($"Initial SOC {initialSoc:F2} kWh, capacity {CapacityKWh:F1} kWh");
            _output.WriteLine("");

            // What production actually planned, for comparison.
            var prodEvening = quarters.Where(q => q.Time >= new DateTime(2026, 8, 11, 18, 0, 0)).ToList();
            _output.WriteLine(
                $"{"PRODUCTION (from the database)",-34} " +
                $"evening: {prodEvening.Count(q => q.Mode == "Discharging"),2} quarters, " +
                $"{prodEvening.Where(q => q.Mode == "Discharging").Sum(q => Math.Abs(q.PlanW) * 0.25) / 1000.0,6:F2} kWh   " +
                $"end SOC {quarters[^1].LeftWh / 1000.0,6:F2} kWh");
            _output.WriteLine("");

            var curve = MeasuredCurve();

            BatterySpec Spec(EfficiencyCurve? efficiency, DischargeCapability? capability) =>
                new(CapacityKWh: CapacityKWh,
                    InitialSocKWh: initialSoc,
                    MaxChargeKW: MaxChargeKW,
                    MaxDischargeKW: MaxDischargeKW,
                    ChargeEfficiency: 0.91,
                    DischargeEfficiency: 0.91,
                    ChargeTaper: null,
                    Efficiency: efficiency,
                    DischargeCapability: capability);

            var capability = new DischargeCapability(PlateauW: 4121, KneeSoc: 0.20, Samples: 748);

            var baseline = new SessyOptions(
                QuarterMinutes: 15,
                CycleCostEurPerKWh: CycleCostEurPerKWh,
                AllowExport: true,
                FutureValueDiscountPerHour: DiscountPerHour,
                ReplacementCostEurPerKWh: ReplacementCostEurPerKWh,
                AllowCarryForward: true);

            // Reproduce first — if this does not match production, the harness is wrong, not the planner.
            Run("baseline (as production)", baseline, Spec(curve, capability), points, bounds, quarters);

            // Then take one input away at a time. Whichever line frees the evening is the cause.
            Run("no carry-forward", baseline with { AllowCarryForward = false }, Spec(curve, capability), points, bounds, quarters);
            Run("no replacement cost", baseline with { ReplacementCostEurPerKWh = 0.0 }, Spec(curve, capability), points, bounds, quarters);
            Run("no discount", baseline with { FutureValueDiscountPerHour = 0.0 }, Spec(curve, capability), points, bounds, quarters);
            Run("no cycle cost", baseline with { CycleCostEurPerKWh = 0.0 }, Spec(curve, capability), points, bounds, quarters);
            Run("flat efficiency 0.91", baseline, Spec(null, capability), points, bounds, quarters);
            Run("no discharge capability", baseline, Spec(curve, null), points, bounds, quarters);
            Run("no soc bounds", baseline, Spec(curve, capability), points,
                bounds.Select(b => b with { MinSocKWh = 0.0 }).ToList(), quarters);

            // The reserve shapes the outcome more than anything else, so pin it at the cap the
            // learned night reserve allows — the highest value production can be using.
            Run("reserve pinned at the cap", baseline, Spec(curve, capability), points,
                bounds.Select(b => b with { MinSocKWh = CapacityKWh * NightReserveCapRatio }).ToList(), quarters);

            // EPEXPricesService marks every quarter of tomorrow as expected once it has ever
            // generated expected prices — the flag is not cleared when the real day-ahead prices
            // arrive. With PredictedPriceMode = Off that makes the whole day reserve-only: no
            // export, no grid charging. If this is what production ran on, it should reproduce it.
            var tomorrowReserveOnly = points
                .Select(p => p.Start.Date == new DateTime(2026, 8, 11) ? p with { ReserveOnly = true } : p)
                .ToList();

            Run("tomorrow marked reserve-only", baseline, Spec(curve, capability),
                tomorrowReserveOnly, bounds, quarters);

            // Does the harness reproduce the household load production planned against? Compare the
            // SOC path with no trading at all: only the ZeroNetHome baseline moves the battery.
            var noTrade = Run("baseline only (no arbitrage)",
                baseline with { AllowExport = false, AllowCarryForward = false },
                Spec(curve, capability), points, bounds, quarters);

            _output.WriteLine("");
            _output.WriteLine("SOC path, harness baseline versus what production planned (kWh):");

            foreach (var step in noTrade.Plan.Where(p => p.Start.Minute == 0 && p.Start.Hour % 3 == 0))
            {
                var production = quarters.First(q => q.Time == step.Start);
                _output.WriteLine(
                    $"  {step.Start:dd-MM HH:mm}  harness {step.SocEndKWh,6:F2}   production {production.LeftWh / 1000.0,6:F2}   " +
                    $"netload {production.NetLoadWh,6:F0} Wh");
            }
        }
    }
}

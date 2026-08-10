using System.Text.Json;

namespace SessyController.Services.Optimization
{
    /// <summary>
    /// Writes the exact input of one planner solve to a JSON file, so a plan that looks wrong can
    /// be replayed outside the running system.
    ///
    /// Why this exists: a plan is decided by four things — price points, battery spec, options and
    /// SOC bounds — and only fragments of them are persisted. Reconstructing the rest from
    /// PlannedQuarters looked convincing and was wrong twice in one afternoon (the two forecast
    /// columns are not even in the same unit). Recording the real input removes the guessing.
    ///
    /// Off unless SESSY_RECORD_SOLVE_INPUTS is set, because it writes a file per rebuild.
    /// </summary>
    public static class SolveInputRecorder
    {
        /// <summary>Environment variable that turns the recorder on.</summary>
        public const string EnableVariable = "SESSY_RECORD_SOLVE_INPUTS";

        /// <summary>Files kept in the directory; the oldest go first.</summary>
        private const int KeepFiles = 20;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        /// <summary>Everything Solve() is given, in one object.</summary>
        public sealed record SolveInput(
            DateTime RecordedAt,
            IReadOnlyList<PricePoint> PricePoints,
            BatterySpec Spec,
            SessyOptions Options,
            IReadOnlyList<SocBound> SocBounds);

        public static bool IsEnabled =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnableVariable));

        /// <summary>
        /// Writes the input and returns the path, or null when the recorder is off or the write
        /// failed. Never throws: a diagnostic must not be able to stop a plan from being built.
        /// </summary>
        public static string? TryWrite(
            string? directory,
            IReadOnlyList<PricePoint> pricePoints,
            BatterySpec spec,
            SessyOptions options,
            IReadOnlyList<SocBound> socBounds,
            DateTime now,
            Action<string>? report = null)
        {
            if (!IsEnabled) return null;

            try
            {
                var target = string.IsNullOrWhiteSpace(directory)
                    ? Directory.GetCurrentDirectory()
                    : directory;

                Directory.CreateDirectory(target);

                var path = Path.Combine(target, $"solve-input-{now:yyyyMMdd-HHmmss}.json");
                var input = new SolveInput(now, pricePoints, spec, options, socBounds);

                File.WriteAllText(path, JsonSerializer.Serialize(input, Json));

                Prune(target);

                report?.Invoke($"Solve input recorded: {path}");

                return path;
            }
            catch (Exception ex)
            {
                report?.Invoke($"Could not record the solve input: {ex.Message}");

                return null;
            }
        }

        /// <summary>Reads a recorded input back, for the replay harness.</summary>
        public static SolveInput? Read(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<SolveInput>(File.ReadAllText(path), Json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Prune(string directory)
        {
            var files = new DirectoryInfo(directory)
                .GetFiles("solve-input-*.json")
                .OrderByDescending(f => f.Name)
                .Skip(KeepFiles)
                .ToList();

            foreach (var file in files)
            {
                try { file.Delete(); } catch (Exception) { /* a diagnostic never fails the caller */ }
            }
        }
    }
}

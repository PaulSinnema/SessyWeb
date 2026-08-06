using SessyCommon.Services;

namespace SessyController.Services
{
    /// <summary>Who is currently driving the batteries.</summary>
    public enum ControlMode
    {
        /// <summary>SessyWeb executes its own MILP plan.</summary>
        SessyWeb,

        /// <summary>SessyWeb executes the manual hour lists from Settings.</summary>
        Manual,

        /// <summary>Charged's own strategy runs on the batteries.</summary>
        Charged,

        /// <summary>The supplier overrode the strategy on the hardware itself.</summary>
        Provider
    }

    /// <summary>
    /// The single source of truth for who is in control.
    ///
    /// There used to be two definitions that disagreed: Settings.WeAreInControl counted manual
    /// override but not the supplier, BatteriesService.WeAreInControl the other way around. The UI
    /// read one and the hardware guards in SessyService the other, which silently swallowed every
    /// command issued under manual override.
    ///
    /// BatteriesService calls Update() once per cycle — it is the only place that knows whether the
    /// supplier took over. Everyone else reads.
    /// </summary>
    public class ControlModeService
    {
        private readonly LoggingService<ControlModeService> _logger;
        private readonly SettingsService _settingsService;

        private ControlMode? _current;

        public ControlModeService(LoggingService<ControlModeService> logger, SettingsService settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        /// <summary>
        /// Before the first Update the supplier is unknown, so the mode is derived from Settings
        /// alone. Charged and manual override are known from the start, which is what matters.
        /// </summary>
        public ControlMode Current => _current ?? Resolve(false);

        /// <summary>
        /// True when SessyWeb may write to the batteries. Manual override is SessyWeb driving too —
        /// a different plan, not a different driver.
        /// </summary>
        public bool WeMayDriveTheBatteries => Current is ControlMode.SessyWeb or ControlMode.Manual;

        /// <summary>Recomputes the mode. Only BatteriesService knows the supplier state.</summary>
        public ControlMode Update(bool supplierInControl)
        {
            var previous = _current;
            var mode = Resolve(supplierInControl);

            _current = mode;

            if (previous != mode)
                _logger.LogWarning($"Control mode: {previous?.ToString() ?? "unknown"} → {mode}");

            return mode;
        }

        private ControlMode Resolve(bool supplierInControl)
        {
            // The supplier wins: it overrides the hardware regardless of what we or Charged want.
            if (supplierInControl) return ControlMode.Provider;

            var settings = _settingsService.Current;

            if (settings == null) return ControlMode.SessyWeb;
            if (settings.ChargedInControl) return ControlMode.Charged;
            if (settings.ManualOverride) return ControlMode.Manual;

            return ControlMode.SessyWeb;
        }
    }
}

using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.Items;

namespace SessyController.Services
{
    /// <summary>
    /// Manages solar inverter curtailment during negative price periods.
    ///
    /// Runs its own 5-second control loop (independent of BatteriesService) so
    /// the inverter output tracks real-time household consumption instead of a
    /// slow estimate.
    ///
    /// Three curtailment modes:
    ///
    ///   1. SHUTDOWN (price negative, battery not full, battery not charging):
    ///      The inverter is set to 0W. Free solar energy from the grid is more
    ///      valuable than generating our own — we get paid to consume, so our
    ///      own production only reduces that benefit.
    ///
    ///   2. THROTTLE (price negative, battery full):
    ///      The inverter output is proportionally reduced to keep net export ≈ 0W.
    ///      The battery cannot absorb more energy, so we limit production to
    ///      exactly what the house consumes at that moment.
    ///
    ///   3. ZERO_EXPORT (price negative, battery charging):
    ///      The inverter is throttled to cover consumption + battery charging power.
    ///      Solar energy is used productively (charges the battery) but nothing
    ///      is exported to the grid at a negative price.
    ///
    /// When neither condition applies, the inverter is restored to 100%.
    ///
    /// Interaction with BatteriesService:
    ///   BatteriesService calls SetCurtailmentRequested(curtailmentNeeded, batteryIsFull)
    ///   each cycle. This service acts on that signal in its own 5-second loop.
    ///   BatteriesService reads IsCurtailmentActive to decide whether to use
    ///   Disabled instead of ZeroNetHome for the Sessy.
    /// </summary>
    public enum CurtailmentMode { None, ZeroExport, Throttle, Shutdown }

    public class InverterCurtailmentService : BackgroundService
    {
        private readonly LoggingService<InverterCurtailmentService> _logger;
        private readonly SolarInverterManager _solarInverterManager;
        private readonly BatteryContainer _batteryContainer;
        private readonly P1MeterContainer _p1MeterContainer;

        // Control loop interval — matches P1 meter polling rate.
        private const int LoopIntervalSeconds = 5;

        // Dead band: ignore net saldo fluctuations smaller than this (W).
        private const double DeadBandW = 50.0;

        // Step size for each adjustment cycle (W).
        private const double StepW = 100.0;

        // Minimum inverter output during throttle mode (W).
        private const double MinThrottleOutputW = 100.0;

        /// <summary>Current curtailment mode for display purposes.</summary>
        public CurtailmentMode CurrentMode { get; private set; } = CurtailmentMode.None;

        /// <summary>
        /// True when the inverter is currently being curtailed (shutdown or throttle).
        /// BatteriesService reads this to substitute Disabled for ZeroNetHome
        /// so Sessy NOM does not fight the curtailment.
        /// </summary>
        public bool IsCurtailmentActive { get; private set; } = false;

        /// <summary>
        /// Last inverter output setpoint sent (W), for diagnostics / UI.
        /// </summary>
        public double CurrentThrottleW { get; private set; } = double.MaxValue;

        // Signals from BatteriesService.
        private volatile bool _priceIsNegative = false;
        private volatile bool _batteryIsFull = false;
        private volatile bool _batteryIsCharging = false;

        public InverterCurtailmentService(
            LoggingService<InverterCurtailmentService> logger,
            SolarInverterManager solarInverterManager,
            BatteryContainer batteryContainer,
            P1MeterContainer p1MeterContainer)
        {
            _logger = logger;
            _solarInverterManager = solarInverterManager;
            _batteryContainer = batteryContainer;
            _p1MeterContainer = p1MeterContainer;
        }

        // ----------------------------------------------------------------
        // API for BatteriesService
        // ----------------------------------------------------------------

        /// <summary>
        /// Called every cycle by BatteriesService to signal the current price
        /// and battery state. The actual inverter control happens in the
        /// background loop at 5-second intervals.
        ///
        /// curtailmentNeeded: true when selling price is negative and battery is not charging.
        /// batteryIsFull:     true when SOC >= 99.5% of capacity.
        /// batteryIsCharging: true when the battery is actively charging.
        /// </summary>
        public void SetCurtailmentRequested(bool curtailmentNeeded, bool batteryIsFull, bool batteryIsCharging)
        {
            _priceIsNegative = curtailmentNeeded;
            _batteryIsFull = batteryIsFull;
            _batteryIsCharging = batteryIsCharging;
        }

        // ----------------------------------------------------------------
        // Background control loop
        // ----------------------------------------------------------------

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("InverterCurtailmentService started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ControlCycleAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"InverterCurtailmentService error: {ex.Message}");

                    // If curtailment should not be active but an error occurred,
                    // reset the active flag so BatteriesService does not keep
                    // forcing Disabled mode indefinitely.
                    if (!_priceIsNegative && IsCurtailmentActive)
                    {
                        _logger.LogWarning("InverterCurtailmentService: resetting IsCurtailmentActive after error.");
                        CurrentThrottleW = double.MaxValue;
                        IsCurtailmentActive = false;
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(LoopIntervalSeconds), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Shutdown — fall through to release curtailment below.
                }
            }

            // Restore inverter on shutdown.
            await ReleaseCurtailmentAsync().ConfigureAwait(false);

            _logger.LogWarning("InverterCurtailmentService stopped.");
        }

        private async Task ControlCycleAsync()
        {
            // ── Mode selection ────────────────────────────────────────────────
            //
            // ZERO_EXPORT: price negative, battery charging.
            //   Solar covers consumption + charging — nothing goes to the grid.
            //   The existing ThrottleInverterAsync already does this via P1 control.
            //
            // SHUTDOWN: price negative, battery not full, battery not charging.
            //   Consuming from the grid is profitable (we get paid), so our own
            //   solar production only reduces that benefit. Switch inverter off.
            //
            // THROTTLE: price negative, battery full.
            //   Battery cannot absorb more. Limit production to house consumption
            //   via proportional P1-based control so we never export.
            //
            // RELEASE: price positive.
            //   Normal operation — restore inverter to 100%.

            if (!_priceIsNegative)
            {
                await ReleaseCurtailmentAsync().ConfigureAwait(false);
                return;
            }

            // ZERO_EXPORT: battery is charging — throttle to cover consumption + charging.
            if (_batteryIsCharging)
            {
                await ThrottleInverterAsync(zeroExport: true).ConfigureAwait(false);
                return;
            }

            // SHUTDOWN: battery not full and not charging.
            if (!_batteryIsFull)
            {
                await ShutdownInverterAsync().ConfigureAwait(false);
                return;
            }

            // THROTTLE: battery full.
            await ThrottleInverterAsync(zeroExport: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Shutdown mode: set inverter output to 0W.
        /// Used when price is negative and battery is not full — consuming from
        /// the grid is more profitable than generating our own solar power.
        /// </summary>
        private async Task ShutdownInverterAsync()
        {
            if (!_solarInverterManager.IsAvailable)
            {
                // Inverter offline — cannot shut down. Log and skip so
                // BatteriesService does not force Disabled mode.
                if (IsCurtailmentActive)
                {
                    _logger.LogWarning("Curtailment SHUTDOWN skipped — inverter offline.");
                    IsCurtailmentActive = false;
                    CurrentThrottleW = double.MaxValue;
                }
                return;
            }

            if (!IsCurtailmentActive || CurrentThrottleW != 0.0)
            {
                _logger.LogInformation("Curtailment SHUTDOWN — inverter set to 0W (price negative, battery not full).");
                await _solarInverterManager.ThrottleInverterToWatts(0.0).ConfigureAwait(false);
                CurrentThrottleW = 0.0;
                IsCurtailmentActive = true;
                CurrentMode = CurtailmentMode.Shutdown;
            }
        }

        /// <summary>
        /// Throttle mode: proportionally reduce inverter output to keep net export ≈ 0W.
        /// Used when price is negative and battery is full (THROTTLE) or charging (ZERO_EXPORT).
        /// In both cases the P1-based control loop keeps net saldo near zero.
        /// </summary>
        private async Task ThrottleInverterAsync(bool zeroExport = false)
        {
            string modeName = zeroExport ? "ZERO_EXPORT" : "THROTTLE";
            // IMPROVEMENT 3: P1 meter call with timeout so that a slow or
            // unresponsive meter does not block the curtailment loop.
            double netSaldoW;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                netSaldoW = await _p1MeterContainer.GetTotalPowerInWatts().WaitAsync(cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("InverterCurtailmentService: P1 meter timeout — skipping cycle.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"InverterCurtailmentService: P1 meter error — skipping cycle. {ex.Message}");
                return;
            }

            // netSaldoW: positive = consuming from grid, negative = exporting to grid.

            double maxCapacityW = _solarInverterManager.TotalCapacity;
            if (maxCapacityW <= 0.0)
            {
                _logger.LogWarning("InverterCurtailmentService: TotalCapacity is 0, skipping cycle.");
                return;
            }

            // On first activation (or transition from shutdown mode), start at full
            // capacity and let the controller step down to the right level.
            if (!IsCurtailmentActive || CurrentThrottleW == 0.0)
            {
                _logger.LogInformation($"Curtailment {modeName} — starting proportional control loop.");
                CurrentThrottleW = maxCapacityW;
                IsCurtailmentActive = true;
                CurrentMode = zeroExport ? CurtailmentMode.ZeroExport : CurtailmentMode.Throttle;
            }

            // ── Proportional step controller ──────────────────────────────────
            double newThrottleW = CurrentThrottleW;

            if (netSaldoW < -DeadBandW)
            {
                double overshootW = Math.Abs(netSaldoW) - DeadBandW;
                double adjustW = Math.Max(StepW, overshootW * 0.5);
                newThrottleW = CurrentThrottleW - adjustW;

                _logger.LogInformation(
                    $"Curtailment {modeName}: net={netSaldoW:F0} W (exporting) → reduce by {adjustW:F0} W");
            }
            else if (netSaldoW > DeadBandW)
            {
                double shortfallW = netSaldoW - DeadBandW;
                double adjustW = Math.Min(StepW, shortfallW);
                newThrottleW = CurrentThrottleW + adjustW;

                _logger.LogInformation(
                    $"Curtailment {modeName}: net={netSaldoW:F0} W (consuming) → increase by {adjustW:F0} W");
            }
            else
            {
                return; // Within dead band — stable.
            }

            newThrottleW = Math.Clamp(newThrottleW, MinThrottleOutputW, maxCapacityW);

            if (Math.Abs(newThrottleW - CurrentThrottleW) > 1.0)
            {
                await _solarInverterManager.ThrottleInverterToWatts(newThrottleW).ConfigureAwait(false);
                CurrentThrottleW = newThrottleW;
            }
        }

        private async Task ReleaseCurtailmentAsync()
        {
            if (!IsCurtailmentActive)
                return;

            _logger.LogInformation("Curtailment RELEASED — restoring inverter to 100%.");
            CurrentMode = CurtailmentMode.None;

            // Retry the restore in case of transient Modbus errors.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await _solarInverterManager.ThrottleInverterToWatts(double.MaxValue).ConfigureAwait(false);
                    CurrentThrottleW = double.MaxValue;
                    IsCurtailmentActive = false;
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"InverterCurtailmentService: release attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }

            // Even if restore failed, reset the active flag so BatteriesService
            // does not keep forcing Disabled mode.
            _logger.LogWarning("InverterCurtailmentService: release failed after 3 attempts — resetting IsCurtailmentActive.");
            CurrentThrottleW = double.MaxValue;
            IsCurtailmentActive = false;
        }
    }
}
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
    /// Strategy:
    ///   When BatteriesService signals that curtailment should be active
    ///   (negative buying price + battery full), this service:
    ///
    ///   1. Reads the actual net power from the P1 meter every 5 seconds.
    ///   2. Adjusts the inverter output in steps so that net export ≈ 0 W.
    ///   3. Sets Sessy to Disabled (StopAll) via the IsCurtailmentActive flag,
    ///      preventing NOM from fighting the curtailment.
    ///
    ///   When curtailment is no longer needed the inverter is restored to 100%.
    ///
    /// Control logic (proportional step controller):
    ///   netSaldo = P1.PowerTotal   (positive = consuming from grid,
    ///                               negative = exporting to grid)
    ///
    ///   Target: netSaldo ≥ 0  (never export)
    ///
    ///   If netSaldo < -DeadBandW  → too much export → reduce inverter output
    ///   If netSaldo >  DeadBandW  → consuming from grid → increase inverter output
    ///   Within dead band          → do nothing (avoids Modbus chatter)
    ///
    /// Interaction with BatteriesService:
    ///   BatteriesService calls SetCurtailmentRequested(true/false) each cycle.
    ///   This service acts on that signal in its own loop.
    ///   BatteriesService reads IsCurtailmentActive to decide whether to use
    ///   Disabled instead of ZeroNetHome for the Sessy.
    /// </summary>
    public class InverterCurtailmentService : BackgroundService
    {
        private readonly LoggingService<InverterCurtailmentService> _logger;
        private readonly SolarInverterManager _solarInverterManager;
        private readonly BatteryContainer _batteryContainer;
        private readonly P1MeterContainer _p1MeterContainer;

        // Control loop interval — matches P1 meter polling rate.
        private const int LoopIntervalSeconds = 5;

        // Dead band: ignore net saldo fluctuations smaller than this (W).
        // Prevents constant Modbus writes for minor load variations.
        private const double DeadBandW = 50.0;

        // Step size for each adjustment cycle (W).
        // Smaller = smoother but slower to converge.
        // Larger = faster but may overshoot.
        private const double StepW = 100.0;

        // Minimum inverter output during curtailment (W).
        // Keeps the inverter in a healthy operating state.
        private const double MinOutputW = 100.0;

        // SOC threshold for battery-full detection.
        private const double FullThresholdRatio = 0.995;

        /// <summary>
        /// True when the inverter is currently being curtailed.
        /// BatteriesService reads this to substitute Disabled for ZeroNetHome
        /// so Sessy NOM does not fight the curtailment.
        /// </summary>
        public bool IsCurtailmentActive { get; private set; } = false;

        /// <summary>
        /// Last inverter output setpoint sent (W), for diagnostics / UI.
        /// </summary>
        public double CurrentThrottleW { get; private set; } = double.MaxValue;

        // Signal from BatteriesService: should curtailment be active?
        private volatile bool _curtailmentRequested = false;

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
        /// Called every cycle by BatteriesService to signal whether curtailment
        /// should be active based on price and battery SOC.
        ///
        /// The actual inverter throttling happens in the background loop at 5-second
        /// intervals so it can react to real-time load changes (heat pump, AC, etc.).
        /// </summary>
        public void SetCurtailmentRequested(bool requested)
        {
            _curtailmentRequested = requested;
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
            if (!_curtailmentRequested)
            {
                await ReleaseCurtailmentAsync().ConfigureAwait(false);
                return;
            }

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

            // Determine current inverter max capacity.
            double maxCapacityW = _solarInverterManager.TotalCapacity;
            if (maxCapacityW <= 0.0)
            {
                _logger.LogWarning("InverterCurtailmentService: TotalCapacity is 0, skipping cycle.");
                return;
            }

            // On first activation, start at full capacity and let the controller
            // step down to the right level.
            if (!IsCurtailmentActive)
            {
                _logger.LogInformation("Curtailment ACTIVATED — starting control loop.");
                CurrentThrottleW = maxCapacityW;
                IsCurtailmentActive = true;
            }

            // ── Proportional step controller ─────────────────────────────
            //
            // netSaldoW < -DeadBandW → exporting too much → reduce output
            // netSaldoW >  DeadBandW → consuming from grid → we can increase output
            // Within dead band       → stable, no change needed
            //
            double newThrottleW = CurrentThrottleW;

            if (netSaldoW < -DeadBandW)
            {
                // Exporting: reduce inverter output by one step.
                // Scale step proportionally to how far outside the dead band we are,
                // so we converge faster when the imbalance is large.
                double overshootW = Math.Abs(netSaldoW) - DeadBandW;
                double adjustW = Math.Max(StepW, overshootW * 0.5);

                newThrottleW = CurrentThrottleW - adjustW;

                _logger.LogInformation(
                    $"Curtailment: net={netSaldoW:F0} W (exporting) → reduce by {adjustW:F0} W");
            }
            else if (netSaldoW > DeadBandW)
            {
                // Consuming from grid: we can allow more inverter output.
                double shortfallW = netSaldoW - DeadBandW;
                double adjustW = Math.Min(StepW, shortfallW);

                newThrottleW = CurrentThrottleW + adjustW;

                _logger.LogInformation(
                    $"Curtailment: net={netSaldoW:F0} W (consuming) → increase by {adjustW:F0} W");
            }
            else
            {
                // Within dead band — stable.
                return;
            }

            // Clamp to [MinOutputW, maxCapacityW].
            newThrottleW = Math.Clamp(newThrottleW, MinOutputW, maxCapacityW);

            // Only write to inverter if setpoint actually changed.
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

            await _solarInverterManager.ThrottleInverterToWatts(double.MaxValue).ConfigureAwait(false);

            CurrentThrottleW = double.MaxValue;
            IsCurtailmentActive = false;
        }
    }
}
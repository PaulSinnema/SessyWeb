using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.Items;
using SessyController.Services.StateMachine;

namespace SessyController.Services
{
    /// <summary>
    /// Executes inverter curtailment based on EnergySystemStateMachine.CurrentAction.
    ///
    /// This service makes NO decisions — it only executes what the state machine prescribes.
    /// All curtailment decisions live in EnergySystemStateMachine.
    ///
    /// Runs a 5-second control loop so the inverter output tracks real-time
    /// household consumption (P1 meter) without waiting for the 60-second
    /// BatteriesService cycle.
    ///
    /// Herstartbaar: the current inverter state is read from HardwareStatusService
    /// on every cycle — no local state flags that become stale after a restart.
    /// </summary>
    public class InverterCurtailmentService : BackgroundService
    {
        private readonly LoggingService<InverterCurtailmentService> _logger;
        private readonly SolarInverterManager _solarInverterManager;
        private readonly P1MeterContainer _p1MeterContainer;
        private readonly EnergySystemStateMachine _stateMachine;
        private readonly HardwareStatusService _hardwareStatus;

        // Control loop interval.
        private const int LoopIntervalSeconds = 5;

        // Dead band: ignore net saldo fluctuations smaller than this (W).
        private const double DeadBandW = 50.0;

        // Step size for each proportional adjustment cycle (W).
        private const double StepW = 100.0;

        // Minimum inverter output during throttle/zero-export mode (W).
        private const double MinThrottleOutputW = 100.0;

        public InverterCurtailmentService(
            LoggingService<InverterCurtailmentService> logger,
            SolarInverterManager solarInverterManager,
            P1MeterContainer p1MeterContainer,
            EnergySystemStateMachine stateMachine,
            HardwareStatusService hardwareStatus)
        {
            _logger = logger;
            _solarInverterManager = solarInverterManager;
            _p1MeterContainer = p1MeterContainer;
            _stateMachine = stateMachine;
            _hardwareStatus = hardwareStatus;
        }

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
                catch (TaskCanceledException) { }
            }

            // Restore inverter to full output on shutdown.
            await ReleaseAsync().ConfigureAwait(false);

            _logger.LogWarning("InverterCurtailmentService stopped.");
        }

        private async Task ControlCycleAsync()
        {
            if (!_hardwareStatus.IsReady)
                return;

            var action = _stateMachine.CurrentAction;

            switch (action.CurtailmentMode)
            {
                case CurtailmentMode.None:
                    await ReleaseAsync().ConfigureAwait(false);
                    break;

                case CurtailmentMode.Shutdown:
                    await ShutdownAsync().ConfigureAwait(false);
                    break;

                case CurtailmentMode.ZeroExport:
                case CurtailmentMode.Throttle:
                    await ThrottleAsync(action.CurtailmentMode).ConfigureAwait(false);
                    break;
            }
        }

        // ── Release ───────────────────────────────────────────────────────────

        private async Task ReleaseAsync()
        {
            // Already at full output — nothing to do.
            if (_hardwareStatus.CurrentInverterSetpointW >= double.MaxValue)
                return;

            _logger.LogInformation("InverterCurtailmentService: RELEASE — restoring inverter to 100%.");

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await _solarInverterManager.ThrottleInverterToWatts(double.MaxValue).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"InverterCurtailmentService: release attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }

            _logger.LogWarning("InverterCurtailmentService: release failed after 3 attempts.");
        }

        // ── Shutdown ──────────────────────────────────────────────────────────

        private async Task ShutdownAsync()
        {
            if (!_solarInverterManager.IsAvailable)
            {
                _logger.LogWarning("InverterCurtailmentService: SHUTDOWN skipped — inverter offline.");
                return;
            }

            // Already shut down — nothing to do.
            if (_hardwareStatus.CurrentInverterSetpointW == 0.0)
                return;

            _logger.LogInformation("InverterCurtailmentService: SHUTDOWN — inverter set to 0W.");
            await _solarInverterManager.ThrottleInverterToWatts(0.0).ConfigureAwait(false);
        }

        // ── P1-based proportional throttle ────────────────────────────────────

        private async Task ThrottleAsync(CurtailmentMode mode)
        {
            if (!_solarInverterManager.IsAvailable)
            {
                _logger.LogWarning($"InverterCurtailmentService: {mode} skipped — inverter offline.");
                return;
            }

            double netSaldoW;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                netSaldoW = await _p1MeterContainer.GetTotalPowerInWatts()
                    .WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("InverterCurtailmentService: P1 meter timeout — skipping cycle.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"InverterCurtailmentService: P1 meter error — {ex.Message}");
                return;
            }

            double maxCapacityW = _solarInverterManager.TotalCapacity;
            if (maxCapacityW <= 0.0)
                return;

            // Current setpoint from hardware status — correct after restarts.
            double currentSetpointW = _hardwareStatus.CurrentInverterSetpointW;

            // On first activation (or after release), start at full capacity.
            if (currentSetpointW >= double.MaxValue)
            {
                _logger.LogInformation($"InverterCurtailmentService: {mode} — starting proportional control loop.");
                currentSetpointW = maxCapacityW;
            }

            // netSaldoW: positive = consuming from grid, negative = exporting.
            double newSetpointW = currentSetpointW;

            if (netSaldoW < -DeadBandW)
            {
                double overshootW = Math.Abs(netSaldoW) - DeadBandW;
                double adjustW = Math.Max(StepW, overshootW * 0.5);
                newSetpointW = currentSetpointW - adjustW;
                _logger.LogInformation($"InverterCurtailmentService {mode}: net={netSaldoW:F0}W (exporting) → reduce by {adjustW:F0}W");
            }
            else if (netSaldoW > DeadBandW)
            {
                double shortfallW = netSaldoW - DeadBandW;
                double adjustW = Math.Min(StepW, shortfallW);
                newSetpointW = currentSetpointW + adjustW;
                _logger.LogInformation($"InverterCurtailmentService {mode}: net={netSaldoW:F0}W (consuming) → increase by {adjustW:F0}W");
            }
            else
            {
                return; // Within dead band — stable.
            }

            newSetpointW = Math.Clamp(newSetpointW, MinThrottleOutputW, maxCapacityW);

            if (Math.Abs(newSetpointW - currentSetpointW) > 1.0)
                await _solarInverterManager.ThrottleInverterToWatts(newSetpointW).ConfigureAwait(false);
        }
    }
}
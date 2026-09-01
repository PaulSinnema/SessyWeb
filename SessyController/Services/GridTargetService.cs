using Microsoft.Extensions.Hosting;
using SessyCommon.Enums;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services.Items;
using SessyController.Services.Optimization;
using SessyController.Services.StateMachine;
using static SessyData.Model.SessyWebControl;

namespace SessyController.Services
{
    /// <summary>
    /// Keeps the P1 grid target in step with what the batteries should (dis)charge.
    ///
    /// In NOM the Sessy holds net = grid_target, so a wanted battery power only lands if the target
    /// tracks the live house net load. BatteriesService picks the mode and power once per 60s cycle;
    /// this loop re-aims the target every 5s so the battery does not drift with the household between
    /// cycles. Only Charging and Discharging track the house; ZeroNetHome posts 0 and Disabled runs
    /// on API with a 0 W setpoint (handled by BatteriesService.ExecuteAction).
    /// </summary>
    public sealed class GridTargetService : BackgroundService
    {
        private readonly LoggingService<GridTargetService> _logger;
        private readonly EnergySystemStateMachine _stateMachine;
        private readonly ControlModeService _controlMode;
        private readonly P1MeterContainer _p1MeterContainer;
        private readonly BatteryContainer _batteryContainer;

        // Only re-post when the target moved more than this, to avoid churning the P1 meter.
        private const int DeadbandW = 50;

        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int? _lastPostedTargetW;

        // The grid target (W) computed this cycle, shown in the UI. In DEBUG it is not written.
        public int? LastComputedTargetW { get; private set; }

        public GridTargetService(LoggingService<GridTargetService> logger,
                                 EnergySystemStateMachine stateMachine,
                                 ControlModeService controlMode,
                                 P1MeterContainer p1MeterContainer,
                                 BatteryContainer batteryContainer)
        {
            _logger = logger;
            _stateMachine = stateMachine;
            _controlMode = controlMode;
            _p1MeterContainer = p1MeterContainer;
            _batteryContainer = batteryContainer;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("GridTargetService started ...");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ApplyForCurrentActionAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogException(ex, "Error refreshing the P1 grid target.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Ignore cancellation during delay.
                }
            }

            _logger.LogWarning("GridTargetService stopped.");
        }

        /// <summary>
        /// Compute and post the grid target for the mode/power currently in CurrentAction.
        /// Runs every 5s here; safe to call from elsewhere too. Does nothing when we are not in
        /// control or the mode is not driven through NOM.
        /// </summary>
        public async Task ApplyForCurrentActionAsync()
        {
            if (!_controlMode.WeMayDriveTheBatteries)
            {
                // Charged has taken over. Don't leave a stale grid target steering the batteries —
                // drive it to 0 (net zero) once. Other control modes (Manual/provider) just stop.
                if (_controlMode.Current == ControlMode.Charged)
                    await PostTargetAsync(0).ConfigureAwait(false);
                else
                    LastComputedTargetW = null;
                return;
            }

            var action = _stateMachine.CurrentAction;

            if (action.BatteryMode != Modes.Charging &&
                action.BatteryMode != Modes.Discharging &&
                action.BatteryMode != Modes.ZeroNetHome)
            {
                // Disabled/Unknown run on API; forget the last target so re-entry always posts.
                _lastPostedTargetW = null;
                LastComputedTargetW = null;
                return;
            }

            int targetW;

            if (action.BatteryMode == Modes.ZeroNetHome)
            {
                targetW = 0;
            }
            else
            {
                var p1NetW = await _p1MeterContainer.GetFirstMeterNetPowerAsync().ConfigureAwait(false);
                if (p1NetW == null)
                {
                    LastComputedTargetW = null;
                    return; // No P1 meter — nothing to drive.
                }

                var batteryW = await _batteryContainer.GetTotalPowerInWatts().ConfigureAwait(false);
                var houseNetW = GridTargetCalculator.HouseNetW(p1NetW.Value, batteryW);

                var maxChargeW = _batteryContainer.GetChargingCapacityInWattsPerHour();
                var maxDischargeW = _batteryContainer.GetDischargingCapacityInWattsPerHour();

                targetW = GridTargetCalculator.GridTargetW(
                    action.BatteryMode, houseNetW, action.BatterySetpointW, maxChargeW, maxDischargeW);
                targetW = GridTargetCalculator.SafeClamp(targetW, houseNetW, maxChargeW, maxDischargeW);
            }

            await PostTargetAsync(targetW).ConfigureAwait(false);
        }

        /// <summary>
        /// Serialise the write, remember it for the UI, and post only when it moved beyond the
        /// deadband. In DEBUG nothing reaches the meter — mirror ExecuteAction's guard.
        /// </summary>
        private async Task PostTargetAsync(int targetW)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                LastComputedTargetW = targetW;

#if !DEBUG
                if (_lastPostedTargetW.HasValue && Math.Abs(targetW - _lastPostedTargetW.Value) <= DeadbandW)
                    return;

                await _p1MeterContainer.SetGridTargetFirstAsync(targetW).ConfigureAwait(false);
                _lastPostedTargetW = targetW;
#endif
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}

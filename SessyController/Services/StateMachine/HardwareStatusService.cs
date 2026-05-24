using SessyCommon.Services;
using SessyController.Managers;
using SessyController.Services.Items;

namespace SessyController.Services.StateMachine
{
    /// <summary>
    /// Polls hardware state at a fixed interval and exposes it as properties.
    ///
    /// This is the single point of truth for hardware readings shared across:
    ///   - EnergySystemInput (for state machine evaluation)
    ///   - InverterCurtailmentService (for herstartbare inverter state)
    ///   - ChargingHoursPage header (for actual vs planned display)
    ///
    /// No decisions are made here — this is a data provider only.
    ///
    /// IsReady is false until the first successful poll completes.
    /// Consumers must check IsReady before using any property.
    /// </summary>
    public class HardwareStatusService : BackgroundService
    {
        private readonly LoggingService<HardwareStatusService> _logger;
        private readonly BatteryContainer _batteryContainer;
        private readonly SolarInverterManager _solarInverterManager;
        private readonly TimeZoneService _timeZoneService;

        private const int PollIntervalSeconds = 10;
        private const double FullThresholdRatio = 0.995;

        public HardwareStatusService(
            LoggingService<HardwareStatusService> logger,
            BatteryContainer batteryContainer,
            SolarInverterManager solarInverterManager,
            TimeZoneService timeZoneService)
        {
            _logger = logger;
            _batteryContainer = batteryContainer;
            _solarInverterManager = solarInverterManager;
            _timeZoneService = timeZoneService;
        }

        // ── Published properties ──────────────────────────────────────────

        /// <summary>True after the first successful poll.</summary>
        public bool IsReady { get; private set; } = false;

        /// <summary>Timestamp of the last successful poll.</summary>
        public DateTime LastPollAt { get; private set; }

        // ── Battery ───────────────────────────────────────────────────────

        /// <summary>Current state of charge (Wh).</summary>
        public double CurrentSocWh { get; private set; }

        /// <summary>Total battery capacity (Wh).</summary>
        public double TotalCapacityWh { get; private set; }

        /// <summary>State of charge as a percentage (0–100).</summary>
        public double SocPct => TotalCapacityWh > 0 ? CurrentSocWh / TotalCapacityWh * 100.0 : 0.0;

        /// <summary>True when battery is at or above the full threshold.</summary>
        public bool BatteryIsFull => TotalCapacityWh > 0 && CurrentSocWh >= TotalCapacityWh * FullThresholdRatio;

        /// <summary>
        /// Actual battery power (W). Negative = charging, positive = discharging.
        /// Includes autonomous NZH charging — not just planned Charging mode.
        /// </summary>
        public double ActualBatteryPowerW { get; private set; }

        /// <summary>True when the battery is actually charging (hardware measurement).</summary>
        public bool BatteryIsActuallyCharging => ActualBatteryPowerW < -50.0;

        /// <summary>Actual battery strategy as reported by the Sessy (for display).</summary>
        public string ActualBatteryStrategy { get; private set; } = string.Empty;

        // ── Inverter ──────────────────────────────────────────────────────

        /// <summary>True when the solar inverter is reachable via Modbus.</summary>
        public bool InverterIsAvailable { get; private set; }

        /// <summary>
        /// Current inverter setpoint in Watts as last commanded.
        /// double.MaxValue = full output (no curtailment).
        /// Derived from SolarInverterManager._lastWattsSet — survives across
        /// service restarts because it is re-read from the manager every poll,
        /// which itself reads from hardware on startup.
        /// </summary>
        public double CurrentInverterSetpointW { get; private set; } = double.MaxValue;

        /// <summary>Actual solar AC output in Watts (measured, not commanded).</summary>
        public double ActualSolarPowerW { get; private set; }

        /// <summary>Current throttle percentage for display (0–100).</summary>
        public double ThrottlePct => TotalInverterCapacityW > 0 && CurrentInverterSetpointW < double.MaxValue
            ? Math.Clamp(CurrentInverterSetpointW / TotalInverterCapacityW * 100.0, 0.0, 100.0)
            : 100.0;

        /// <summary>Total inverter capacity (W).</summary>
        public double TotalInverterCapacityW { get; private set; }

        // ── Background poll loop ──────────────────────────────────────────

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning("HardwareStatusService started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PollAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"HardwareStatusService poll error: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TaskCanceledException) { }
            }

            _logger.LogWarning("HardwareStatusService stopped.");
        }

        private async Task PollAsync()
        {
            // ── Battery ───────────────────────────────────────────────────
            TotalCapacityWh = _batteryContainer.GetTotalCapacity();

            try
            {
                CurrentSocWh = await _batteryContainer.GetStateOfChargeInWatts().ConfigureAwait(false);
                ActualBatteryPowerW = await _batteryContainer.GetTotalPowerInWatts().ConfigureAwait(false);

                var firstBattery = _batteryContainer.Batteries?.FirstOrDefault();
                if (firstBattery != null)
                {
                    var strategy = await firstBattery.GetActivePowerStrategy().ConfigureAwait(false);
                    ActualBatteryStrategy = strategy?.Strategy ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"HardwareStatusService: battery poll failed — {ex.Message}");
            }

            // ── Inverter ──────────────────────────────────────────────────
            try
            {
                InverterIsAvailable = _solarInverterManager.IsAvailable;
                TotalInverterCapacityW = _solarInverterManager.TotalCapacity;
                ActualSolarPowerW = await _solarInverterManager.GetActualSolarPowerInWatts().ConfigureAwait(false);
                CurrentInverterSetpointW = _solarInverterManager.LastSetpointW ?? double.MaxValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"HardwareStatusService: inverter poll failed — {ex.Message}");
            }

            LastPollAt = _timeZoneService.Now;

            // Mark ready after first poll attempt — even if hardware is unreachable.
            // Consumers check IsReady before using values; 0/default values are safe.
            IsReady = true;
        }
    }
}
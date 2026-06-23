using Microsoft.Extensions.Logging;
using SessyCommon.Extensions;
using SessyCommon.Services;
using SessyController.Services;
using SessyController.Interfaces;
using SessyController.Services.Items;
using static SessyController.Services.Items.ChargingModes;
using SessyCommon.Enums;

namespace SessyController.Services.StateMachine
{
    /// <summary>
    /// All inputs needed by EnergySystemStateMachine, gathered from live services.
    ///
    /// Call LoadAsync() once per cycle. The object reflects a single consistent
    /// snapshot in time — immutable after loading.
    ///
    /// Uses HardwareStatusService for all hardware state so that multiple background
    /// services and the view layer share the same polled values without issuing
    /// redundant requests to the hardware.
    ///
    /// The selling price comes from BatteriesService.GetQuarterlyInfos() which already
    /// holds the fully calculated (all-in) prices for each quarter.
    /// </summary>
    public class EnergySystemInput
    {
        private readonly HardwareStatusService _hardwareStatus;
        private readonly IMilpService _milpService;
        private readonly BatteriesService _batteriesService;
        private readonly TimeZoneService _timeZoneService;
        private readonly ILogger<EnergySystemInput>? _logger;



        public EnergySystemInput(
            HardwareStatusService hardwareStatus,
            IMilpService milpService,
            BatteriesService batteriesService,
            TimeZoneService timeZoneService,
            ILogger<EnergySystemInput>? logger = null)
        {
            _hardwareStatus = hardwareStatus;
            _milpService = milpService;
            _batteriesService = batteriesService;
            _timeZoneService = timeZoneService;
            _logger = logger;
        }

        // ── Snapshot values (set by LoadAsync) ───────────────────────────

        /// <summary>Quarter being evaluated.</summary>
        public virtual DateTime NowQuarter { get; protected set; }

        /// <summary>What MILP has planned for this quarter.</summary>
        public virtual Modes PlannedMode { get; protected set; }

        /// <summary>MILP planned setpoint in Watts.</summary>
        public virtual double PlannedSetpointW { get; protected set; }

        /// <summary>All-in selling price for this quarter (EUR/kWh).</summary>
        public virtual double SellingPriceEurPerKWh { get; protected set; }

        /// <summary>True when the selling price is negative — exporting costs money.</summary>
        public virtual bool SellingPriceIsNegative => SellingPriceEurPerKWh < 0.0;

        /// <summary>Current state of charge (Wh).</summary>
        public virtual double CurrentSocWh { get; protected set; }

        /// <summary>Alias for CurrentSocWh — used in snapshot writing for clarity.</summary>
        public double ActualSocWh => CurrentSocWh;

        /// <summary>Total battery capacity (Wh).</summary>
        public virtual double TotalCapacityWh { get; protected set; }

        /// <summary>Maximum combined charge power across all batteries (W).</summary>
        public virtual double MaxChargeSetpointW { get; protected set; }

        /// <summary>True when the battery is at or above the full threshold.</summary>
        public virtual bool BatteryIsFull => TotalCapacityWh > 0 &&
                                     CurrentSocWh >= TotalCapacityWh * BatteryConstants.FullThresholdRatio;

        /// <summary>
        /// Actual battery power (W) as reported by hardware.
        /// Negative = charging, positive = discharging.
        /// Includes autonomous NZH charging — not just planned Charging mode.
        /// </summary>
        public virtual double ActualBatteryPowerW { get; protected set; }

        /// <summary>
        /// True when the battery is actually charging according to hardware measurement.
        /// Uses actual power, NOT the planned mode — so NZH autonomous charging is included.
        /// </summary>
        public virtual bool BatteryIsActuallyCharging => ActualBatteryPowerW < BatteryConstants.ChargingThresholdW;

        /// <summary>True when the solar inverter is reachable via Modbus.</summary>
        public virtual bool InverterIsAvailable { get; protected set; }

        /// <summary>True after LoadAsync() completes successfully.</summary>
        public virtual bool IsLoaded { get; protected set; }

        // ── Loading ───────────────────────────────────────────────────────

        /// <summary>
        /// Loads all inputs from live services in one consistent snapshot.
        /// Must be called once per evaluation cycle.
        /// Returns without setting IsLoaded when HardwareStatusService is not yet ready.
        /// </summary>
        public async Task LoadAsync()
        {
            IsLoaded = false;

            if (!_hardwareStatus.IsReady)
                return;

            NowQuarter = _timeZoneService.Now.DateFloorQuarter();

            // ── MILP plan for this quarter ────────────────────────────────
            (PlannedMode, PlannedSetpointW) = await _milpService
                .GetExecutableActionForNowAsync(NowQuarter)
                .ConfigureAwait(false);

            // ── Selling price from quarterly infos ────────────────────────
            // BatteriesService already holds the fully calculated all-in prices.
            var quarterlyInfos = _batteriesService.GetQuarterlyInfos();
            var nowQi = quarterlyInfos.FirstOrDefault(q => q.Time == NowQuarter);
            if (nowQi == null)
                _logger?.LogWarning($"EnergySystemInput: no QuarterlyInfo found for {NowQuarter:dd-MM HH:mm} — SellingPrice defaults to 0.0, curtailment may be skipped.");
            SellingPriceEurPerKWh = nowQi?.SellingPrice ?? 0.0;

            // ── Hardware state — all from HardwareStatusService ───────────
            CurrentSocWh = _hardwareStatus.CurrentSocWh;
            TotalCapacityWh = _hardwareStatus.TotalCapacityWh;
            MaxChargeSetpointW = _hardwareStatus.MaxChargeSetpointW;
            ActualBatteryPowerW = _hardwareStatus.ActualBatteryPowerW;
            InverterIsAvailable = _hardwareStatus.InverterIsAvailable;

            IsLoaded = true;
        }
    }
}
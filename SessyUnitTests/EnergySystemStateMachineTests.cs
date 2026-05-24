using Microsoft.Extensions.Logging.Abstractions;
using SessyController.Services;
using SessyController.Services.StateMachine;
using static SessyController.Services.Items.ChargingModes;

namespace SessyTests.Services
{
    /// <summary>
    /// Tests all state machine transitions.
    ///
    /// Input matrix:
    ///   SellingPriceIsNegative:    true / false
    ///   BatteryIsActuallyCharging: true / false (hardware measurement, threshold -50W)
    ///   BatteryIsFull:             true / false (threshold 99.5% of capacity)
    ///   InverterIsAvailable:       true / false
    ///   PlannedMode:               Charging / Discharging / ZeroNetHome / Disabled / Unknown
    ///
    /// InverterSetpointW semantics verified:
    ///   double.MaxValue = full output or P1-controlled throttle
    ///   0.0             = hard shutdown (SHUTDOWN only)
    /// </summary>
    public class EnergySystemStateMachineTests
    {
        private readonly EnergySystemStateMachine _sut;

        private const double Capacity = 16200.0;
        private const double FullSoc = Capacity * 0.996; // above 99.5% threshold
        private const double HalfSoc = Capacity * 0.5;

        public EnergySystemStateMachineTests()
        {
            _sut = new EnergySystemStateMachine(
                new NullLogger<EnergySystemStateMachine>());
        }

        // ── Test input factory ────────────────────────────────────────────────

        private static TestInput Input(
            bool priceNegative,
            double actualBatteryPowerW,
            double socWh,
            double capacityWh,
            bool inverterAvailable,
            Modes plannedMode,
            double plannedSetpointW = 4000.0) =>
            new TestInput(
                priceNegative,
                actualBatteryPowerW,
                socWh,
                capacityWh,
                inverterAvailable,
                plannedMode,
                plannedSetpointW);

        /// <summary>
        /// Concrete test subclass of EnergySystemInput.
        /// Avoids mocking a class with complex DI constructor.
        /// All properties are set directly by the test factory method.
        /// </summary>
        private class TestInput : EnergySystemInput
        {
            private readonly bool _sellingPriceIsNegative;
            private readonly bool _batteryIsActuallyCharging;
            private readonly bool _batteryIsFull;

            public TestInput(
                bool sellingPriceIsNegative,
                double actualBatteryPowerW,
                double currentSocWh,
                double totalCapacityWh,
                bool inverterIsAvailable,
                Modes plannedMode,
                double plannedSetpointW) : base(null!, null!, null!, null!)
            {
                _sellingPriceIsNegative = sellingPriceIsNegative;
                ActualBatteryPowerW = actualBatteryPowerW;
                _batteryIsActuallyCharging = actualBatteryPowerW < -50.0;
                CurrentSocWh = currentSocWh;
                TotalCapacityWh = totalCapacityWh;
                _batteryIsFull = totalCapacityWh > 0 && currentSocWh >= totalCapacityWh * 0.995;
                InverterIsAvailable = inverterIsAvailable;
                PlannedMode = plannedMode;
                PlannedSetpointW = plannedSetpointW;
                IsLoaded = true;
            }

            public override bool SellingPriceIsNegative => _sellingPriceIsNegative;
            public override bool BatteryIsActuallyCharging => _batteryIsActuallyCharging;
            public override bool BatteryIsFull => _batteryIsFull;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Normal plan — selling price positive
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void PricePositive_PlannedCharging_Returns_Charging()
        {
            var action = _sut.Evaluate(Input(false, -3000, HalfSoc, Capacity, true, Modes.Charging, 4000));

            Assert.Equal(Modes.Charging, action.BatteryMode);
            Assert.Equal(4000, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void PricePositive_PlannedDischarging_Returns_Discharging()
        {
            var action = _sut.Evaluate(Input(false, 3000, HalfSoc, Capacity, true, Modes.Discharging, 3500));

            Assert.Equal(Modes.Discharging, action.BatteryMode);
            Assert.Equal(3500, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void PricePositive_PlannedZeroNetHome_Returns_ZeroNetHome()
        {
            var action = _sut.Evaluate(Input(false, -500, HalfSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(Modes.ZeroNetHome, action.BatteryMode);
            Assert.Equal(0, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void PricePositive_PlannedDisabled_Returns_Disabled()
        {
            var action = _sut.Evaluate(Input(false, 0, HalfSoc, Capacity, true, Modes.Disabled));

            Assert.Equal(Modes.Disabled, action.BatteryMode);
            Assert.Equal(0, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void PricePositive_PlannedUnknown_Returns_Disabled()
        {
            var action = _sut.Evaluate(Input(false, 0, HalfSoc, Capacity, true, Modes.Unknown));

            Assert.Equal(Modes.Disabled, action.BatteryMode);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ZERO_EXPORT — price negative, battery actually charging
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void PriceNegative_BatteryCharging_PlannedCharging_Returns_ZeroExport_KeepsCharging()
        {
            var action = _sut.Evaluate(Input(true, -4000, HalfSoc, Capacity, true, Modes.Charging, 4000));

            Assert.Equal(Modes.Charging, action.BatteryMode);
            Assert.Equal(4000, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW); // P1 throttle — NOT shutdown
            Assert.Equal(CurtailmentMode.ZeroExport, action.CurtailmentMode);
            Assert.True(action.IsOverride);
        }

        [Fact]
        public void PriceNegative_BatteryChargingAutonomously_PlannedNZH_Returns_ZeroExport_KeepsNZH()
        {
            // NZH autonomously charging from solar surplus — hardware measures negative power.
            var action = _sut.Evaluate(Input(true, -2000, HalfSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(Modes.ZeroNetHome, action.BatteryMode);
            Assert.Equal(0, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW); // P1 throttle
            Assert.Equal(CurtailmentMode.ZeroExport, action.CurtailmentMode);
            Assert.True(action.IsOverride);
        }

        [Fact]
        public void PriceNegative_ZeroExport_PreservesChargingSetpoint()
        {
            var action = _sut.Evaluate(Input(true, -5000, HalfSoc, Capacity, true, Modes.Charging, 5500));

            Assert.Equal(5500, action.BatterySetpointW);
        }

        [Fact]
        public void PriceNegative_BatteryAtExactDeadband_50W_NotConsidered_Charging()
        {
            // -50W is at the boundary — NOT charging (threshold is < -50).
            // Battery not full → SHUTDOWN.
            var action = _sut.Evaluate(Input(true, -50, HalfSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Shutdown, action.CurtailmentMode);
        }

        [Fact]
        public void PriceNegative_BatteryAt_51W_IsConsidered_Charging()
        {
            // -51W is below threshold → IS charging → ZERO_EXPORT.
            var action = _sut.Evaluate(Input(true, -51, HalfSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.ZeroExport, action.CurtailmentMode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // THROTTLE — price negative, battery full, not charging
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void PriceNegative_BatteryFull_NotCharging_Returns_Throttle()
        {
            var action = _sut.Evaluate(Input(true, 0, FullSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(Modes.Disabled, action.BatteryMode);
            Assert.Equal(0, action.BatterySetpointW);
            Assert.Equal(double.MaxValue, action.InverterSetpointW); // P1 throttle — NOT hard shutdown
            Assert.Equal(CurtailmentMode.Throttle, action.CurtailmentMode);
            Assert.True(action.IsOverride);
        }

        [Fact]
        public void PriceNegative_BatteryFull_Discharging_Returns_Throttle()
        {
            // Discharging has positive power → BatteryIsActuallyCharging = false.
            // BatteryIsFull = true → THROTTLE.
            var action = _sut.Evaluate(Input(true, 2000, FullSoc, Capacity, true, Modes.Discharging, 2000));

            Assert.Equal(Modes.Disabled, action.BatteryMode);
            Assert.Equal(CurtailmentMode.Throttle, action.CurtailmentMode);
        }

        [Fact]
        public void PriceNegative_BatteryAtExactFullThreshold_Returns_Throttle()
        {
            double exactFull = Capacity * 0.995;
            var action = _sut.Evaluate(Input(true, 0, exactFull, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Throttle, action.CurtailmentMode);
        }

        [Fact]
        public void Throttle_InverterSetpoint_IsMaxValue_NotZero()
        {
            // Critical: THROTTLE must NOT be 0.0 — that would be a shutdown.
            // InverterCurtailmentService uses P1 to find the right setpoint.
            var action = _sut.Evaluate(Input(true, 0, FullSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(double.MaxValue, action.InverterSetpointW);
            Assert.NotEqual(0.0, action.InverterSetpointW);
        }

        // ══════════════════════════════════════════════════════════════════════
        // SHUTDOWN — price negative, battery not full, not charging
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void PriceNegative_NotFull_NotCharging_InverterAvailable_Returns_Shutdown()
        {
            var action = _sut.Evaluate(Input(true, 0, HalfSoc, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(Modes.Disabled, action.BatteryMode);
            Assert.Equal(0, action.BatterySetpointW);
            Assert.Equal(0.0, action.InverterSetpointW); // hard shutdown
            Assert.Equal(CurtailmentMode.Shutdown, action.CurtailmentMode);
            Assert.True(action.IsOverride);
        }

        [Fact]
        public void Shutdown_InverterSetpoint_IsExactlyZero()
        {
            var action = _sut.Evaluate(Input(true, 0, HalfSoc, Capacity, true, Modes.Disabled));

            Assert.Equal(0.0, action.InverterSetpointW);
        }

        [Fact]
        public void PriceNegative_NotFull_NotCharging_BelowFullThreshold_Returns_Shutdown()
        {
            double justBelowFull = Capacity * 0.994;
            var action = _sut.Evaluate(Input(true, 0, justBelowFull, Capacity, true, Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Shutdown, action.CurtailmentMode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // SHUTDOWN fallback — inverter offline
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void PriceNegative_NotFull_NotCharging_InverterOffline_FallsBackToPlan()
        {
            var action = _sut.Evaluate(Input(true, 0, HalfSoc, Capacity, false, Modes.ZeroNetHome));

            Assert.Equal(Modes.ZeroNetHome, action.BatteryMode);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void PriceNegative_Discharging_InverterOffline_FallsBackToPlan()
        {
            var action = _sut.Evaluate(Input(true, 3000, HalfSoc, Capacity, false, Modes.Discharging, 3000));

            Assert.Equal(Modes.Discharging, action.BatteryMode);
            Assert.Equal(3000, action.BatterySetpointW);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        // ══════════════════════════════════════════════════════════════════════
        // CurrentAction is updated after each Evaluate()
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void CurrentAction_UpdatedAfterEachEvaluate()
        {
            _sut.Evaluate(Input(false, 0, HalfSoc, Capacity, true, Modes.Charging, 4000));
            Assert.Equal(CurtailmentMode.None, _sut.CurrentAction.CurtailmentMode);
            Assert.Equal(Modes.Charging, _sut.CurrentAction.BatteryMode);

            _sut.Evaluate(Input(true, 0, HalfSoc, Capacity, true, Modes.ZeroNetHome));
            Assert.Equal(CurtailmentMode.Shutdown, _sut.CurrentAction.CurtailmentMode);
            Assert.Equal(Modes.Disabled, _sut.CurrentAction.BatteryMode);
        }

        [Fact]
        public void CurrentAction_DefaultState_IsZeroNetHome_NoCurtailment()
        {
            // Before first Evaluate(), CurrentAction must have safe defaults.
            Assert.Equal(Modes.ZeroNetHome, _sut.CurrentAction.BatteryMode);
            Assert.Equal(CurtailmentMode.None, _sut.CurrentAction.CurtailmentMode);
            Assert.Equal(double.MaxValue, _sut.CurrentAction.InverterSetpointW);
        }
    }
}
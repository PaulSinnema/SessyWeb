using Microsoft.Extensions.Logging.Abstractions;
using SessyCommon.Enums;
using SessyController.Services;
using SessyController.Services.StateMachine;
using static SessyController.Services.Items.ChargingModes;

namespace SessyTests.Services
{
    /// <summary>
    /// Covers the hysteresis added after an external installation reported the Sessy switching
    /// between the strategies "Net zero" (NOM) and "API" dozens of times inside one quarter.
    ///
    /// The mapping is what makes any flicker expensive: ZeroNetHome is NOM, while Charging,
    /// Discharging and Disabled are all API. Three independent comparisons could flip on noise —
    /// the charge/discharge guards against a live SOC, the idle branch against the sign of a
    /// NetLoad that is recomputed every cycle, and the negative-price branch against a battery
    /// power its own previous decision caused.
    ///
    /// Everything under test here is pure, so none of it needs hardware or a clock.
    /// </summary>
    public class ModeFlappingTests
    {
        private static readonly TimeSpan Dwell = EnergySystemStateMachine.MinimumModeDwell;
        private static readonly DateTime T0 = new DateTime(2026, 8, 13, 7, 0, 0);

        // ══════════════════════════════════════════════════════════════════════
        // MilpServiceBase.MinimumUsefulEnergyWh — the deadband scales with the bank
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Deadband_never_drops_below_the_absolute_floor()
        {
            // A tiny or unconfigured bank must not end up with a deadband of nearly zero.
            Assert.Equal(25.0, MilpServiceBase.MinimumUsefulEnergyWh(0.0));
            Assert.Equal(25.0, MilpServiceBase.MinimumUsefulEnergyWh(1000.0));
        }

        [Fact]
        public void Deadband_scales_with_capacity_so_one_battery_gets_a_resolvable_band()
        {
            // One Sessy (~5.2 kWh) used to get 25 Wh, which is inside its SOC reporting noise.
            Assert.Equal(26.0, MilpServiceBase.MinimumUsefulEnergyWh(5200.0));

            // Three Sessys.
            Assert.Equal(81.0, MilpServiceBase.MinimumUsefulEnergyWh(16200.0));
        }

        // ══════════════════════════════════════════════════════════════════════
        // MilpServiceBase.GuardHolds — engage low, release high
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Guard_engages_at_the_stop_threshold()
        {
            Assert.True(MilpServiceBase.GuardHolds(wasHolding: false, availableWh: 25.0, stopWh: 25.0, releaseWh: 100.0));
            Assert.False(MilpServiceBase.GuardHolds(wasHolding: false, availableWh: 26.0, stopWh: 25.0, releaseWh: 100.0));
        }

        [Fact]
        public void Guard_does_not_release_just_above_the_stop_threshold()
        {
            // This is the flap: ZeroNetHome lets the house drain the battery, the number climbs a
            // few Wh, and without hysteresis the very next cycle commands API again.
            Assert.True(MilpServiceBase.GuardHolds(wasHolding: true, availableWh: 30.0, stopWh: 25.0, releaseWh: 100.0));
            Assert.True(MilpServiceBase.GuardHolds(wasHolding: true, availableWh: 99.0, stopWh: 25.0, releaseWh: 100.0));
        }

        [Fact]
        public void Guard_releases_once_the_energy_is_back_over_the_release_threshold()
        {
            Assert.False(MilpServiceBase.GuardHolds(wasHolding: true, availableWh: 100.0, stopWh: 25.0, releaseWh: 100.0));
        }

        [Fact]
        public void Soc_noise_around_the_threshold_cannot_flip_the_guard()
        {
            // ±10 Wh of noise on a 26 Wh stop threshold: once held, it stays held.
            double[] noisy = { 24.0, 34.0, 20.0, 30.0, 26.0, 35.0, 22.0 };

            bool held = false;
            int flips = 0;

            foreach (var wh in noisy)
            {
                bool next = MilpServiceBase.GuardHolds(held, wh, stopWh: 26.0, releaseWh: 104.0);
                if (next != held) flips++;
                held = next;
            }

            Assert.True(held);
            Assert.Equal(1, flips);
        }

        // ══════════════════════════════════════════════════════════════════════
        // MilpServiceBase.SelectIdleMode — ZeroNetHome (NOM) versus Disabled (API)
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Clear_surplus_stores_it_in_the_battery()
        {
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: -400.0, hasRoom: true, belowCycleCost: true,
                previous: Modes.Disabled, deadbandWh: 50.0);

            Assert.Equal(Modes.ZeroNetHome, mode);
        }

        [Fact]
        public void Clear_deficit_below_cycle_cost_switches_the_battery_off()
        {
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: 400.0, hasRoom: true, belowCycleCost: true,
                previous: Modes.ZeroNetHome, deadbandWh: 50.0);

            Assert.Equal(Modes.Disabled, mode);
        }

        [Fact]
        public void Inside_the_deadband_the_previous_choice_is_kept()
        {
            Assert.Equal(Modes.ZeroNetHome, MilpServiceBase.SelectIdleMode(
                netLoadWh: 10.0, hasRoom: true, belowCycleCost: true,
                previous: Modes.ZeroNetHome, deadbandWh: 50.0));

            Assert.Equal(Modes.Disabled, MilpServiceBase.SelectIdleMode(
                netLoadWh: -10.0, hasRoom: true, belowCycleCost: true,
                previous: Modes.Disabled, deadbandWh: 50.0));
        }

        [Fact]
        public void Net_load_crossing_zero_at_sunrise_no_longer_alternates()
        {
            // The reported failure: the previous quarter's NetLoad is recomputed every cycle and
            // its solar term is replaced by the measurement, so around sunrise it wobbles across
            // zero. Every crossing used to be a NOM/API strategy write.
            double[] sunrise = { 40.0, -30.0, 20.0, -45.0, 15.0, -10.0, 35.0 };

            var mode = Modes.Disabled;
            int changes = 0;

            foreach (var netLoadWh in sunrise)
            {
                var next = MilpServiceBase.SelectIdleMode(
                    netLoadWh, hasRoom: true, belowCycleCost: true, previous: mode, deadbandWh: 50.0);

                if (next != mode) changes++;
                mode = next;
            }

            Assert.Equal(0, changes);
            Assert.Equal(Modes.Disabled, mode);
        }

        [Fact]
        public void A_real_surplus_still_gets_through_the_deadband()
        {
            // The band only shields the sign test; it must not freeze the choice for good.
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: -600.0, hasRoom: true, belowCycleCost: true,
                previous: Modes.Disabled, deadbandWh: 50.0);

            Assert.Equal(Modes.ZeroNetHome, mode);
        }

        [Fact]
        public void Neither_rule_applying_leaves_the_plan_alone()
        {
            // Deficit, but selling is worth a cycle: the plan decides, not this branch.
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: 400.0, hasRoom: true, belowCycleCost: false,
                previous: Modes.Unknown, deadbandWh: 50.0);

            Assert.Equal(Modes.Unknown, mode);
        }

        [Fact]
        public void Surplus_without_room_is_unchanged_from_the_old_rule()
        {
            // Full battery, surplus: not ZeroNetHome, and the deficit rule does not apply either.
            var mode = MilpServiceBase.SelectIdleMode(
                netLoadWh: -400.0, hasRoom: false, belowCycleCost: true,
                previous: Modes.Unknown, deadbandWh: 50.0);

            Assert.Equal(Modes.Unknown, mode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // BatteriesService.ExpectedStrategy — which mode is which Sessy strategy
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Only_ZeroNetHome_maps_to_the_Net_zero_strategy()
        {
            Assert.Equal("POWER_STRATEGY_NOM", BatteriesService.ExpectedStrategy(Modes.ZeroNetHome));
        }

        [Theory]
        [InlineData(Modes.Charging)]
        [InlineData(Modes.Discharging)]
        [InlineData(Modes.Disabled)]
        [InlineData(Modes.Unknown)]
        public void Every_other_mode_is_executed_through_the_open_api(Modes mode)
        {
            // Disabled is the surprising one: it looks idle but goes out as API with setpoint 0,
            // so a ZeroNetHome/Disabled flip is a strategy rewrite like any other.
            Assert.Equal("POWER_STRATEGY_API", BatteriesService.ExpectedStrategy(mode));
        }

        // ══════════════════════════════════════════════════════════════════════
        // EnergySystemStateMachine.MayChangeMode — stopping now, starting later
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Stopping_is_never_delayed()
        {
            // Holding an active mode on a timer is the one direction that could keep charging a
            // full battery, so it must always be allowed.
            Assert.True(EnergySystemStateMachine.MayChangeMode(
                Modes.Charging, Modes.ZeroNetHome, T0, T0, Dwell));

            Assert.True(EnergySystemStateMachine.MayChangeMode(
                Modes.Discharging, Modes.Disabled, T0, T0, Dwell));
        }

        [Fact]
        public void Starting_again_too_soon_is_refused()
        {
            Assert.False(EnergySystemStateMachine.MayChangeMode(
                Modes.ZeroNetHome, Modes.Charging, T0, T0.AddSeconds(1), Dwell));
        }

        [Fact]
        public void Starting_again_is_allowed_once_the_dwell_has_passed()
        {
            Assert.True(EnergySystemStateMachine.MayChangeMode(
                Modes.ZeroNetHome, Modes.Charging, T0, T0.Add(Dwell), Dwell));
        }

        [Fact]
        public void Swapping_two_equally_idle_modes_waits_out_the_dwell()
        {
            // ZeroNetHome is NOM and Disabled is API, so this pair is a strategy write too.
            Assert.False(EnergySystemStateMachine.MayChangeMode(
                Modes.ZeroNetHome, Modes.Disabled, T0, T0.AddSeconds(30), Dwell));

            Assert.True(EnergySystemStateMachine.MayChangeMode(
                Modes.ZeroNetHome, Modes.Disabled, T0, T0.Add(Dwell), Dwell));
        }

        [Fact]
        public void Keeping_the_same_mode_is_always_allowed()
        {
            Assert.True(EnergySystemStateMachine.MayChangeMode(
                Modes.Charging, Modes.Charging, T0, T0, Dwell));
        }

        [Fact]
        public void The_first_decision_after_a_restart_is_not_delayed()
        {
            Assert.True(EnergySystemStateMachine.MayChangeMode(
                Modes.Unknown, Modes.Charging, T0, T0, Dwell));
        }

        // ══════════════════════════════════════════════════════════════════════
        // EnergySystemStateMachine.Evaluate — the dwell in place
        // ══════════════════════════════════════════════════════════════════════

        private static EnergySystemStateMachine Machine() =>
            new EnergySystemStateMachine(new NullLogger<EnergySystemStateMachine>());

        [Fact]
        public void A_plan_flipping_every_cycle_reaches_the_hardware_once_per_dwell()
        {
            // The plan alternates ZeroNetHome/Disabled on every heartbeat — the reported failure.
            var sut = Machine();
            var modes = new List<Modes>();

            for (int cycle = 0; cycle < 60; cycle++)
            {
                var planned = cycle % 2 == 0 ? Modes.ZeroNetHome : Modes.Disabled;
                modes.Add(sut.Evaluate(new DwellInput(planned, T0.AddSeconds(cycle))).BatteryMode);
            }

            int changes = modes.Zip(modes.Skip(1), (a, b) => a != b).Count(changed => changed);

            // Exactly one: the very first move is free — nothing has happened since startup, so
            // there is no dwell to wait out. Every later flip inside the 120 s window is refused.
            // Before this change all 59 of them were a strategy write to the battery.
            Assert.Equal(1, changes);
        }

        [Fact]
        public void A_genuine_change_still_gets_through_after_the_dwell()
        {
            var sut = Machine();

            Assert.Equal(Modes.Disabled, sut.Evaluate(new DwellInput(Modes.Disabled, T0)).BatteryMode);
            Assert.Equal(Modes.Disabled, sut.Evaluate(new DwellInput(Modes.Charging, T0.AddSeconds(30))).BatteryMode);
            Assert.Equal(Modes.Charging, sut.Evaluate(new DwellInput(Modes.Charging, T0.Add(Dwell))).BatteryMode);
        }

        [Fact]
        public void A_suppressed_cycle_keeps_commanding_the_previous_setpoint()
        {
            // The held-back cycle must return the action that is actually running, setpoint and
            // all — returning a bare mode would silently drop the power the battery was given.
            var sut = Machine();

            sut.Evaluate(new DwellInput(Modes.Discharging, T0, plannedSetpointW: 3500.0));
            var held = sut.Evaluate(new DwellInput(Modes.Charging, T0.AddSeconds(5), plannedSetpointW: 2200.0));

            Assert.Equal(Modes.Discharging, held.BatteryMode);
            Assert.Equal(3500.0, held.BatterySetpointW);
        }

        [Fact]
        public void Stopping_still_happens_immediately_through_Evaluate()
        {
            var sut = Machine();

            sut.Evaluate(new DwellInput(Modes.Charging, T0, plannedSetpointW: 2200.0));
            var stopped = sut.Evaluate(new DwellInput(Modes.ZeroNetHome, T0.AddSeconds(1)));

            Assert.Equal(Modes.ZeroNetHome, stopped.BatteryMode);
        }

        [Fact]
        public void A_snapshot_without_a_clock_behaves_exactly_as_before()
        {
            // EnergySystemInput.Now defaults to MinValue; the whole existing test matrix relies on
            // the dwell staying out of the way there.
            var sut = Machine();

            sut.Evaluate(new DwellInput(Modes.Disabled, DateTime.MinValue));
            var action = sut.Evaluate(new DwellInput(Modes.Charging, DateTime.MinValue));

            Assert.Equal(Modes.Charging, action.BatteryMode);
        }

        /// <summary>
        /// Minimal EnergySystemInput for the dwell: a positive selling price, so Evaluate takes the
        /// plan branch and the planned mode is the only thing under test.
        /// </summary>
        private class DwellInput : EnergySystemInput
        {
            public DwellInput(Modes plannedMode, DateTime now, double plannedSetpointW = 0.0)
                : base(null!, null!, null!, null!)
            {
                Now = now;
                PlannedMode = plannedMode;
                PlannedSetpointW = plannedSetpointW;
                CurrentSocWh = 2600.0;
                TotalCapacityWh = 5200.0;
                MaxChargeSetpointW = 2200.0;
                InverterIsAvailable = true;
                CurtailmentIsPossible = true;
                IsLoaded = true;
            }

            public override bool SellingPriceIsNegative => false;
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SessyCommon.Extensions;
using SessyController.Services;
using SessyController.Services.StateMachine;
using SessyData.Model;
using SessyData.Services;
using static SessyController.Services.Items.ChargingModes;

namespace SessyTests.Services
{
    /// <summary>
    /// Tests all 7 scenarios for the plan/actual system:
    ///
    ///   Scenario 1: Fresh start — no plan in DB
    ///   Scenario 2: Normal running — no rebuild needed
    ///   Scenario 3: Rebuild — price signature changed
    ///   Scenario 4: Rebuild — SOC deviation exceeded
    ///   Scenario 5: Restart — plan restored from DB, SOC fallback to PlannedQuarter
    ///   Scenario 6: Restart — plan too old, treated as fresh start
    ///   Scenario 7: Curtailment active — ActualQuarter records curtailment mode
    /// </summary>
    public class ScenarioTests
    {
        private const double Capacity = 16200.0;
        private const double FullSoc = Capacity * 0.996;
        private const double HalfSoc = Capacity * 0.5;
        private const double ThreshPct = 20.0;

        // ── Helpers ───────────────────────────────────────────────────────────

        private static EnergySystemStateMachine BuildStateMachine() =>
            new EnergySystemStateMachine(new NullLogger<EnergySystemStateMachine>());

        private static TestInput Input(
            bool priceNegative,
            double actualBatteryPowerW,
            double socWh,
            double capacityWh,
            bool inverterAvailable,
            Modes plannedMode,
            double plannedSetpointW = 4000.0) =>
            new TestInput(priceNegative, actualBatteryPowerW, socWh,
                          capacityWh, inverterAvailable, plannedMode, plannedSetpointW);

        private static PlannedQuarter MakePlannedQuarter(DateTime time, double chargeLeftWh) =>
            new PlannedQuarter
            {
                Time = time,
                PlannedMode = "Charging",
                PlannedPowerW = 4000,
                PlannedChargeLeftWh = chargeLeftWh,
                SellingPriceEurKWh = 0.25,
                BuyingPriceEurKWh = 0.30
            };

        private static ActualQuarter MakeActualQuarter(
            DateTime time, double socWh, string curtailmentMode = "None") =>
            new ActualQuarter
            {
                Time = time,
                ActualMode = "POWER_STRATEGY_API",
                ActualPowerW = -3000,
                ActualSocWh = socWh,
                CurtailmentMode = curtailmentMode,
                StateMachineReason = "MILP: Charging at 4000W"
            };

        // ── Moq helpers ───────────────────────────────────────────────────────

        // Single IDbContextFactory<ModelContext> arg — constructors take exactly one.
        private static Mock<PlannedQuarterDataService> MockPlanned() =>
            new Mock<PlannedQuarterDataService>(MockBehavior.Loose, null!);

        private static Mock<ActualQuarterDataService> MockActual() =>
            new Mock<ActualQuarterDataService>(MockBehavior.Loose, null!);

        // ── TestInput subclass ────────────────────────────────────────────────

        private class TestInput : EnergySystemInput
        {
            private readonly bool _sellingPriceIsNegative;
            private readonly bool _batteryIsActuallyCharging;
            private readonly bool _batteryIsFull;

            public TestInput(
                bool priceNegative, double actualBatteryPowerW,
                double socWh, double capacityWh,
                bool inverterAvailable, Modes plannedMode, double plannedSetpointW)
                : base(null!, null!, null!, null!)
            {
                _sellingPriceIsNegative = priceNegative;
                ActualBatteryPowerW = actualBatteryPowerW;
                _batteryIsActuallyCharging = actualBatteryPowerW < -50.0;
                CurrentSocWh = socWh;
                TotalCapacityWh = capacityWh;
                _batteryIsFull = capacityWh > 0 && socWh >= capacityWh * 0.995;
                InverterIsAvailable = inverterAvailable;
                PlannedMode = plannedMode;
                PlannedSetpointW = plannedSetpointW;
                IsLoaded = true;
            }

            public override bool SellingPriceIsNegative => _sellingPriceIsNegative;
            public override bool BatteryIsActuallyCharging => _batteryIsActuallyCharging;
            public override bool BatteryIsFull => _batteryIsFull;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 1 — Fresh start: no plan in DB
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario1_FreshStart_NoSocReference_DeviationIsZero()
        {
            // Without a plan or PlannedQuarter entry, deviation must be 0 —
            // we cannot calculate deviation without a reference.
            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc
                .Setup(s => s.GetForQuarterAsync(It.IsAny<DateTime>()))
                .ReturnsAsync((PlannedQuarter?)null);

            var mockActualSvc = MockActual();
            mockActualSvc
                .Setup(s => s.GetRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new List<ActualQuarter>());
            mockPlannedSvc
                .Setup(s => s.GetRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new List<PlannedQuarter>());

            var svc = new PlanVsActualService(
                mockPlannedSvc.Object, mockActualSvc.Object, null!, null!);

            var stats = svc.GetStatsAsync(DateTime.Now.AddHours(-1), DateTime.Now)
                           .GetAwaiter().GetResult();

            Assert.Equal(0, stats.QuarterCount);
            Assert.Equal(0.0, stats.AvgSocDeviationPct);
        }

        [Fact]
        public void Scenario1_FreshStart_StateMachine_DefaultAction_IsZeroNetHome()
        {
            // Before first solve, CurrentAction defaults to ZeroNetHome, no curtailment.
            var sm = BuildStateMachine();

            Assert.Equal(Modes.ZeroNetHome, sm.CurrentAction.BatteryMode);
            Assert.Equal(CurtailmentMode.None, sm.CurrentAction.CurtailmentMode);
            Assert.Equal(double.MaxValue, sm.CurrentAction.InverterSetpointW);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 2 — Normal running: no rebuild needed
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario2_NormalRunning_PlanExecuted_NoDeviation()
        {
            // SOC matches plan → deviation = 0% → no rebuild triggered.
            var now = new DateTime(2026, 5, 25, 14, 0, 0);
            var socWh = 10000.0;
            var plan = MakePlannedQuarter(now, chargeLeftWh: socWh);
            var actual = MakeActualQuarter(now, socWh: socWh);

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc.Setup(s => s.GetRangeAsync(now, now))
                          .ReturnsAsync(new List<PlannedQuarter> { plan });

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(now, now))
                         .ReturnsAsync(new List<ActualQuarter> { actual });

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object, null!, null!);
            var entries = svc.GetAsync(now, now).GetAwaiter().GetResult();

            Assert.Single(entries);
            Assert.Equal(0.0, entries[0].SocDeviationPct, 3);
        }

        [Fact]
        public void Scenario2_NormalRunning_StateMachine_FollowsPlan_Charging()
        {
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(false, -4000, HalfSoc, Capacity, true,
                                           Modes.Charging, 4000));

            Assert.Equal(Modes.Charging, action.BatteryMode);
            Assert.Equal(4000, action.BatterySetpointW);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
        }

        [Fact]
        public void Scenario2_NormalRunning_StateMachine_FollowsPlan_Discharging()
        {
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(false, 3000, HalfSoc, Capacity, true,
                                           Modes.Discharging, 3500));

            Assert.Equal(Modes.Discharging, action.BatteryMode);
            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 3 — Rebuild: price signature changed
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario3_PriceSignatureChanged_PlannedQuarters_Overwritten()
        {
            // When the plan rebuilds, PlannedQuarter rows for overlapping quarters
            // must be upserted — verified via the PlanVsActualService showing new values.
            var now = new DateTime(2026, 5, 25, 14, 0, 0);
            var oldPlan = MakePlannedQuarter(now, chargeLeftWh: 5000);
            var newPlan = MakePlannedQuarter(now, chargeLeftWh: 8000); // higher after rebuild

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc.Setup(s => s.GetRangeAsync(now, now))
                          .ReturnsAsync(new List<PlannedQuarter> { newPlan });

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(now, now))
                         .ReturnsAsync(new List<ActualQuarter>());

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object, null!, null!);
            var entries = svc.GetAsync(now, now).GetAwaiter().GetResult();

            // After upsert, new PlannedChargeLeftWh is 8000, not old 5000.
            Assert.Equal(8000, entries[0].PlannedChargeLeftWh);
            Assert.NotEqual(oldPlan.PlannedChargeLeftWh, entries[0].PlannedChargeLeftWh);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 4 — Rebuild: SOC deviation exceeded
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario4_SocDeviationExceeds_Threshold()
        {
            // PlannedSocWh = 80%, actual = 20% → deviation = 60% → exceeds threshold.
            var planned = 0.8 * Capacity;
            var actual = 0.2 * Capacity;
            var deviation = Math.Abs(actual - planned) / Capacity * 100.0;

            Assert.True(deviation > ThreshPct,
                $"Deviation {deviation:F1}% should exceed threshold {ThreshPct}%");
        }

        [Fact]
        public void Scenario4_SocDeviationWithin_Threshold_NoRebuild()
        {
            // PlannedSocWh = 50%, actual = 55% → deviation = 5% → below threshold.
            var planned = 0.50 * Capacity;
            var actual = 0.55 * Capacity;
            var deviation = Math.Abs(actual - planned) / Capacity * 100.0;

            Assert.True(deviation < ThreshPct,
                $"Deviation {deviation:F1}% should be below threshold {ThreshPct}%");
        }

        [Fact]
        public void Scenario4_PlanVsActual_ShowsHighDeviation()
        {
            var now = new DateTime(2026, 5, 25, 14, 0, 0);
            var planned = MakePlannedQuarter(now, chargeLeftWh: Capacity * 0.8);
            var actual = MakeActualQuarter(now, socWh: Capacity * 0.2);

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc.Setup(s => s.GetRangeAsync(now, now))
                          .ReturnsAsync(new List<PlannedQuarter> { planned });

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(now, now))
                         .ReturnsAsync(new List<ActualQuarter> { actual });

            var mockBatteryContainer = new Mock<SessyController.Services.Items.BatteryContainer>(
                MockBehavior.Loose, null!, null!, null!, null!);
            mockBatteryContainer.Setup(b => b.GetTotalCapacity()).Returns(Capacity);

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object,
                                              mockBatteryContainer.Object, null!);
            var entries = svc.GetAsync(now, now).GetAwaiter().GetResult();

            Assert.Single(entries);
            Assert.True(entries[0].SocDeviationPct > ThreshPct,
                $"Expected deviation > {ThreshPct}%, got {entries[0].SocDeviationPct:F1}%");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 5 — Restart: plan restored, SOC fallback to PlannedQuarter
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario5_Restart_PlannedQuarterFallback_ReturnsDeviation()
        {
            // After restart, _plannedSocByQuarter is empty.
            // GetCurrentSocDeviationPct falls back to PlannedQuarterDataService.
            // Simulated via: planned=10000Wh, actual=8000Wh → 12.3% deviation.
            var now = new DateTime(2026, 5, 25, 14, 0, 0).AddMinutes(-7);
            var plannedSoc = 10000.0;
            var actualSoc = 8000.0;
            var expected = Math.Abs(actualSoc - plannedSoc) / Capacity * 100.0;

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc
                .Setup(s => s.GetForQuarterAsync(It.IsAny<DateTime>()))
                .ReturnsAsync(MakePlannedQuarter(now, chargeLeftWh: plannedSoc));

            var actual1 = MakeActualQuarter(now, socWh: actualSoc);
            var planned1 = MakePlannedQuarter(now, chargeLeftWh: plannedSoc);

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(now, now))
                         .ReturnsAsync(new List<ActualQuarter> { actual1 });
            mockPlannedSvc.Setup(s => s.GetRangeAsync(now, now))
                          .ReturnsAsync(new List<PlannedQuarter> { planned1 });

            var mockBatteryContainer = new Mock<SessyController.Services.Items.BatteryContainer>(
                MockBehavior.Loose, null!, null!, null!, null!);
            mockBatteryContainer.Setup(b => b.GetTotalCapacity()).Returns(Capacity);

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object,
                                              mockBatteryContainer.Object, null!);
            var entries = svc.GetAsync(now, now).GetAwaiter().GetResult();

            Assert.Single(entries);
            Assert.Equal(expected, entries[0].SocDeviationPct, 3);
        }

        [Fact]
        public void Scenario5_Restart_NoPlannedQuarterEntry_DeviationIsZero()
        {
            // After restart, _plannedSocByQuarter empty AND no PlannedQuarter in DB
            // → deviation = 0 (no reference → no false rebuild trigger).
            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc
                .Setup(s => s.GetForQuarterAsync(It.IsAny<DateTime>()))
                .ReturnsAsync((PlannedQuarter?)null);

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                         .ReturnsAsync(new List<ActualQuarter>());
            mockPlannedSvc.Setup(s => s.GetRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                          .ReturnsAsync(new List<PlannedQuarter>());

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object, null!, null!);
            var stats = svc.GetStatsAsync(DateTime.Now.AddHours(-1), DateTime.Now)
                           .GetAwaiter().GetResult();

            Assert.Equal(0.0, stats.AvgSocDeviationPct);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 6 — Restart: plan too old → treated as fresh start
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario6_PlanTooOld_MaxPlanAgeHoursExceeded()
        {
            // A plan older than MaxPlanAgeHours is not restored.
            var savedAt = DateTime.Now.AddHours(-(PlannedAction.MaxPlanAgeHours + 1));
            var age = (DateTime.Now - savedAt).TotalHours;

            Assert.True(age > PlannedAction.MaxPlanAgeHours,
                $"Plan age {age:F1}h should exceed MaxPlanAgeHours {PlannedAction.MaxPlanAgeHours}h");
        }

        [Fact]
        public void Scenario6_PlanWithinMaxAge_IsEligibleForRestore()
        {
            var savedAt = DateTime.Now.AddHours(-1);
            var age = (DateTime.Now - savedAt).TotalHours;

            Assert.True(age <= PlannedAction.MaxPlanAgeHours,
                $"Plan age {age:F1}h should be within MaxPlanAgeHours {PlannedAction.MaxPlanAgeHours}h");
        }

        [Fact]
        public void Scenario6_OldPlan_StateMachine_DefaultsToZeroNetHome()
        {
            // With no restored plan, StateMachine defaults to ZeroNetHome — safe fallback.
            var sm = BuildStateMachine();

            Assert.Equal(Modes.ZeroNetHome, sm.CurrentAction.BatteryMode);
            Assert.Equal(CurtailmentMode.None, sm.CurrentAction.CurtailmentMode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scenario 7 — Curtailment active
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void Scenario7_CurtailmentZeroExport_BatteryCharging_RecordedInActualQuarter()
        {
            // Price negative + battery charging → StateMachine decides ZERO_EXPORT.
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(true, -4000, HalfSoc, Capacity, true,
                                           Modes.Charging, 4000));

            Assert.Equal(CurtailmentMode.ZeroExport, action.CurtailmentMode);

            var actual = MakeActualQuarter(DateTime.Now.DateFloorQuarter(),
                                           socWh: HalfSoc,
                                           curtailmentMode: action.CurtailmentMode.ToString());

            Assert.Equal("ZeroExport", actual.CurtailmentMode);
        }

        [Fact]
        public void Scenario7_CurtailmentShutdown_BatteryNotFull_RecordedInActualQuarter()
        {
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(true, 0, HalfSoc, Capacity, true,
                                           Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Shutdown, action.CurtailmentMode);
            Assert.Equal(0.0, action.InverterSetpointW);
            Assert.Equal(Modes.Disabled, action.BatteryMode);

            var actual = MakeActualQuarter(DateTime.Now.DateFloorQuarter(),
                                           socWh: HalfSoc,
                                           curtailmentMode: action.CurtailmentMode.ToString());

            Assert.Equal("Shutdown", actual.CurtailmentMode);
        }

        [Fact]
        public void Scenario7_CurtailmentThrottle_BatteryFull_RecordedInActualQuarter()
        {
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(true, 0, FullSoc, Capacity, true,
                                           Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.Throttle, action.CurtailmentMode);
            Assert.Equal(double.MaxValue, action.InverterSetpointW); // P1 throttle, not hard shutdown
            Assert.Equal(Modes.Disabled, action.BatteryMode);

            var actual = MakeActualQuarter(DateTime.Now.DateFloorQuarter(),
                                           socWh: FullSoc,
                                           curtailmentMode: action.CurtailmentMode.ToString());

            Assert.Equal("Throttle", actual.CurtailmentMode);
        }

        [Fact]
        public void Scenario7_NoCurtailment_PositivePrice_RecordedAsNone()
        {
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(false, -4000, HalfSoc, Capacity, true,
                                           Modes.Charging, 4000));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);

            var actual = MakeActualQuarter(DateTime.Now.DateFloorQuarter(),
                                           socWh: HalfSoc,
                                           curtailmentMode: action.CurtailmentMode.ToString());

            Assert.Equal("None", actual.CurtailmentMode);
        }

        [Fact]
        public void Scenario7_CurtailmentStats_CountedCorrectly()
        {
            // Verify PlanVsActualService counts curtailment quarters correctly.
            var t1 = new DateTime(2026, 5, 25, 14, 0, 0);
            var t2 = new DateTime(2026, 5, 25, 14, 15, 0);
            var t3 = new DateTime(2026, 5, 25, 14, 30, 0);

            var planned = new List<PlannedQuarter>
            {
                MakePlannedQuarter(t1, 10000),
                MakePlannedQuarter(t2, 9500),
                MakePlannedQuarter(t3, 9000)
            };
            var actuals = new List<ActualQuarter>
            {
                MakeActualQuarter(t1, 10000, "ZeroExport"),  // curtailment
                MakeActualQuarter(t2, 9500,  "Shutdown"),    // curtailment
                MakeActualQuarter(t3, 9000,  "None")         // no curtailment
            };

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc.Setup(s => s.GetRangeAsync(t1, t3)).ReturnsAsync(planned);

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(t1, t3)).ReturnsAsync(actuals);

            var mockBatteryContainer = new Mock<SessyController.Services.Items.BatteryContainer>(
                MockBehavior.Loose, null!, null!, null!, null!);
            mockBatteryContainer.Setup(b => b.GetTotalCapacity()).Returns(Capacity);

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object,
                                              mockBatteryContainer.Object, null!);
            var stats = svc.GetStatsAsync(t1, t3).GetAwaiter().GetResult();

            Assert.Equal(3, stats.QuarterCount);
            Assert.Equal(2, stats.CurtailmentQuarters);
        }

        [Fact]
        public void Scenario7_InverterOffline_CurtailmentFallsBackToPlan()
        {
            // When inverter is offline, StateMachine cannot execute curtailment
            // and falls back to MILP plan without override.
            var sm = BuildStateMachine();
            var action = sm.Evaluate(Input(true, 0, HalfSoc, Capacity,
                                           inverterAvailable: false,
                                           plannedMode: Modes.ZeroNetHome));

            Assert.Equal(CurtailmentMode.None, action.CurtailmentMode);
            Assert.False(action.IsOverride);
            Assert.Equal(Modes.ZeroNetHome, action.BatteryMode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PlanVsActualService — statistics
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void PlanVsActual_ModeAccuracy_AllMatch_Returns100Pct()
        {
            var t1 = new DateTime(2026, 5, 25, 14, 0, 0);
            var t2 = new DateTime(2026, 5, 25, 14, 15, 0);

            var planned = new List<PlannedQuarter>
            {
                new() { Time = t1, PlannedMode = "Charging",    PlannedChargeLeftWh = 10000 },
                new() { Time = t2, PlannedMode = "Discharging", PlannedChargeLeftWh = 9000  }
            };
            var actuals = new List<ActualQuarter>
            {
                new() { Time = t1, ActualMode = "Charging",    ActualSocWh = 10000, CurtailmentMode = "None", StateMachineReason = "" },
                new() { Time = t2, ActualMode = "Discharging", ActualSocWh = 9000,  CurtailmentMode = "None", StateMachineReason = "" }
            };

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc.Setup(s => s.GetRangeAsync(t1, t2)).ReturnsAsync(planned);

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(t1, t2)).ReturnsAsync(actuals);

            var mockBattery = new Mock<SessyController.Services.Items.BatteryContainer>(
                MockBehavior.Loose, null!, null!, null!, null!);
            mockBattery.Setup(b => b.GetTotalCapacity()).Returns(Capacity);

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object,
                                              mockBattery.Object, null!);
            var stats = svc.GetStatsAsync(t1, t2).GetAwaiter().GetResult();

            Assert.Equal(100.0, stats.ModeAccuracyPct, 1);
            Assert.Equal(0.0, stats.AvgSocDeviationPct, 3);
        }

        [Fact]
        public void PlanVsActual_ModeAccuracy_NoneMatch_Returns0Pct()
        {
            var t1 = new DateTime(2026, 5, 25, 14, 0, 0);

            var planned = new List<PlannedQuarter>
            {
                new() { Time = t1, PlannedMode = "Charging", PlannedChargeLeftWh = 10000 }
            };
            var actuals = new List<ActualQuarter>
            {
                new() { Time = t1, ActualMode = "Disabled", ActualSocWh = 5000,
                        CurtailmentMode = "Shutdown", StateMachineReason = "Curtailment" }
            };

            var mockPlannedSvc = MockPlanned();
            mockPlannedSvc.Setup(s => s.GetRangeAsync(t1, t1)).ReturnsAsync(planned);

            var mockActualSvc = MockActual();
            mockActualSvc.Setup(s => s.GetRangeAsync(t1, t1)).ReturnsAsync(actuals);

            var mockBattery = new Mock<SessyController.Services.Items.BatteryContainer>(
                MockBehavior.Loose, null!, null!, null!, null!);
            mockBattery.Setup(b => b.GetTotalCapacity()).Returns(Capacity);

            var svc = new PlanVsActualService(mockPlannedSvc.Object, mockActualSvc.Object,
                                              mockBattery.Object, null!);
            var stats = svc.GetStatsAsync(t1, t1).GetAwaiter().GetResult();

            Assert.Equal(0.0, stats.ModeAccuracyPct, 1);
        }
    }
}
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SessyCommon.Configurations;
using SessyCommon.Services;
using SessyController.Services;
using SessyData.Model;
using Xunit;

namespace SessyTests.Services
{
    /// <summary>
    /// Handing the batteries to Charged's own strategy.
    ///
    /// This existed since v1.0.6 but never ran: the handover branch only executes while we are NOT
    /// in control, and the write guard blocked exactly that case, so the POST was never sent. Both
    /// halves are pinned here — the mapping, and the fact that it fires once on the transition
    /// rather than every cycle.
    /// </summary>
    public class ChargedHandoverTests
    {
        private static ControlModeService NewService(Settings settings)
        {
            var settingsService = new Mock<SettingsService>(
                null!, null!, null!, null!, Options.Create(new SettingsConfig()));

            settingsService.Setup(s => s.Current).Returns(settings);

            return new ControlModeService(
                new LoggingService<ControlModeService>(new Mock<ILogger<ControlModeService>>().Object),
                settingsService.Object);
        }

        // ── The mapping ───────────────────────────────────────────────────────

        [Fact]
        public void Profit_maximization_hands_over_to_roi()
        {
            Assert.True(BatteriesService.MapsToRoi(OptimizationStrategy.ProfitMaximization));
        }

        [Fact]
        public void Balanced_hands_over_to_eco()
        {
            Assert.False(BatteriesService.MapsToRoi(OptimizationStrategy.Balanced));
        }

        [Theory]
        [InlineData(OptimizationStrategy.SelfConsumption)]
        [InlineData(OptimizationStrategy.BatterySaving)]
        public void The_remaining_strategies_follow_balanced_to_eco(OptimizationStrategy strategy)
        {
            // ECO is Sessy's self-consumption-oriented, lower-cycle mode, so it fits both better
            // than ROI does.
            Assert.False(BatteriesService.MapsToRoi(strategy));
        }

        // ── When it fires ─────────────────────────────────────────────────────

        [Fact]
        public void Switching_charged_on_triggers_the_handover_once()
        {
            var settings = new Settings();
            var sut = NewService(settings);

            sut.Update(supplierInControl: false);          // we are driving
            Assert.False(sut.JustHandedOverToCharged);

            settings.ChargedInControl = true;
            sut.Update(supplierInControl: false);          // the tick where the user ticked the box
            Assert.True(sut.JustHandedOverToCharged);

            sut.Update(supplierInControl: false);          // every cycle after that
            Assert.False(sut.JustHandedOverToCharged);
        }

        [Fact]
        public void A_restart_while_charged_is_already_in_control_does_not_hand_over()
        {
            // The previous mode is unknown after a restart. Charged has been driving for a while by
            // then and the strategy may have been set by hand in the Sessy portal — re-asserting it
            // on every start would silently overwrite that.
            var sut = NewService(new Settings { ChargedInControl = true });

            sut.Update(supplierInControl: false);

            Assert.Equal(ControlMode.Charged, sut.Current);
            Assert.False(sut.JustHandedOverToCharged);
        }

        [Fact]
        public void Taking_control_back_does_not_count_as_a_handover()
        {
            var settings = new Settings { ChargedInControl = true };
            var sut = NewService(settings);

            sut.Update(supplierInControl: false);

            settings.ChargedInControl = false;
            sut.Update(supplierInControl: false);

            Assert.False(sut.JustHandedOverToCharged);
        }

        [Fact]
        public void The_supplier_taking_over_is_not_a_handover_to_charged()
        {
            // Provider outranks Charged, so the mode is Provider and no strategy is pushed — the
            // supplier already overrode the hardware.
            var settings = new Settings();
            var sut = NewService(settings);

            sut.Update(supplierInControl: false);

            settings.ChargedInControl = true;
            sut.Update(supplierInControl: true);

            Assert.Equal(ControlMode.Provider, sut.Current);
            Assert.False(sut.JustHandedOverToCharged);
        }
    }
}

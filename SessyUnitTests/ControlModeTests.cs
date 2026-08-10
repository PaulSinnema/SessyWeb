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
    /// Who is driving the batteries.
    ///
    /// There used to be two definitions that disagreed: Settings.WeAreInControl counted manual
    /// override but not the supplier, BatteriesService.WeAreInControl the other way around. The
    /// hardware write guards read the first, so every command issued under manual override was
    /// silently swallowed — the batteries kept doing whatever they were last told. That regression
    /// is what Manual_override_may_still_drive_the_batteries pins down.
    /// </summary>
    public class ControlModeTests
    {
        private static ControlModeService NewService(Settings settings)
        {
            var settingsService = new Mock<SettingsService>(null!, null!, null!, null!);

            settingsService.Setup(s => s.Current).Returns(settings);

            var logger = new LoggingService<ControlModeService>(
                new Mock<ILogger<ControlModeService>>().Object);

            return new ControlModeService(logger, settingsService.Object);
        }

        private static Settings Config(bool charged = false, bool manual = false) =>
            new() { ChargedInControl = charged, ManualOverride = manual };

        // ── Priority ──────────────────────────────────────────────────────────

        [Fact]
        public void Nothing_set_is_SessyWeb()
        {
            var sut = NewService(Config());

            Assert.Equal(ControlMode.SessyWeb, sut.Update(supplierInControl: false));
        }

        [Fact]
        public void The_supplier_wins_from_everything()
        {
            // It overrides the strategy on the hardware itself, so what we or Charged want is moot.
            var sut = NewService(Config(charged: true, manual: true));

            Assert.Equal(ControlMode.Provider, sut.Update(supplierInControl: true));
        }

        [Fact]
        public void Charged_wins_from_manual_override()
        {
            var sut = NewService(Config(charged: true, manual: true));

            Assert.Equal(ControlMode.Charged, sut.Update(supplierInControl: false));
        }

        [Fact]
        public void Manual_override_alone_is_manual()
        {
            var sut = NewService(Config(manual: true));

            Assert.Equal(ControlMode.Manual, sut.Update(supplierInControl: false));
        }

        // ── May we write to the hardware ──────────────────────────────────────

        [Fact]
        public void Manual_override_may_still_drive_the_batteries()
        {
            // The regression: manual override is SessyWeb driving, a different plan rather than a
            // different driver. Blocking it here made every manual command a no-op.
            var sut = NewService(Config(manual: true));

            sut.Update(supplierInControl: false);

            Assert.True(sut.WeMayDriveTheBatteries);
        }

        [Fact]
        public void We_may_drive_when_nobody_else_does()
        {
            var sut = NewService(Config());

            sut.Update(supplierInControl: false);

            Assert.True(sut.WeMayDriveTheBatteries);
        }

        [Fact]
        public void Charged_blocks_writing()
        {
            var sut = NewService(Config(charged: true));

            sut.Update(supplierInControl: false);

            Assert.False(sut.WeMayDriveTheBatteries);
        }

        [Fact]
        public void The_supplier_blocks_writing()
        {
            var sut = NewService(Config());

            sut.Update(supplierInControl: true);

            Assert.False(sut.WeMayDriveTheBatteries);
        }

        // ── Before the first update ───────────────────────────────────────────

        [Fact]
        public void Before_the_first_update_the_mode_follows_the_settings()
        {
            // BatteriesService is the only caller of Update, and its first cycle comes after the UI
            // is already answering questions. Charged and manual override are known from Settings
            // straight away; only the supplier has to wait for a poll.
            Assert.Equal(ControlMode.Charged, NewService(Config(charged: true)).Current);
            Assert.Equal(ControlMode.Manual, NewService(Config(manual: true)).Current);
            Assert.Equal(ControlMode.SessyWeb, NewService(Config()).Current);
        }

        [Fact]
        public void Charged_blocks_writing_before_the_first_update_too()
        {
            // Otherwise one cycle's worth of commands still reaches batteries we handed over.
            Assert.False(NewService(Config(charged: true)).WeMayDriveTheBatteries);
        }
    }
}

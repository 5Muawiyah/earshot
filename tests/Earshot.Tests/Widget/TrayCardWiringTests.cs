using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tests.Phase1;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Widget;

// What the tray hands its cards, proved through the real TrayContext on a private desktop with only the outside replaced:
// a look change from Windows reaches the open card and redraws it once, a card opened from the keyboard shows its focus
// visual from the start, and every card the tray makes has motion. A tray that stops wiring one of them fails here.
[TestClass]
public sealed class TrayCardWiringTests
{
    private const int WmSettingChange = 0x001A;

    private sealed class FakeLookSource : ISystemLookSource
    {
        public double TextScaleFactor { get; set; } = 1.0;

        public bool AdvancedEffectsEnabled { get; set; } = true;

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private static Action<EarshotSettings> WidgetOn(bool leftClickConnects = true) =>
        s => s.Widget = s.Widget with { Enabled = true, ShowOnTaskbar = true, CaseOpenCardOn = true, LeftClickConnects = leftClickConnects };

    private static void OpenCard(TrayHarness tray)
    {
        tray.PumpUntilIdle();
        tray.Context.RequestWidgetCardForTest();
        TrayHarness.PumpUntil(() => tray.Context.WidgetCardIsShownForTest, "The card never opened.");
    }

    // ----- the look -----

    [TestMethod]
    public void ALookChangeFromWindowsReachesTheOpenCardAndRedrawsItOnce()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var source = new FakeLookSource();
            using var look = new SystemLookService(source, () => false, static a => a(), new CapturingLog());
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(), lookService: look);
            OpenCard(tray);
            int before = tray.Context.WidgetCardLookReappliesForTest;

            source.TextScaleFactor = 1.5;
            source.Raise();

            TrayHarness.PumpUntil(() => tray.Context.WidgetCardLookReappliesForTest > before, "The look change never reached the open card.");
            tray.PumpUntilIdle();
            Assert.AreEqual(before + 1, tray.Context.WidgetCardLookReappliesForTest, "One look change, one redraw.");
        });
    }

    [TestMethod]
    public void ASettingsChangeMessageThatChangesTheLookRedrawsTheOpenCardOnceAndOneThatDoesNotStillRedrawsIt()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var source = new FakeLookSource();
            bool highContrast = false;
            using var look = new SystemLookService(source, () => highContrast, static a => a(), new CapturingLog());
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(), lookService: look);
            OpenCard(tray);

            // A theme or accent change: no look the service reads has changed, and the card is drawn again once.
            int before = tray.Context.WidgetCardLookReappliesForTest;
            var accent = Message.Create(tray.Context.Window.Handle, WmSettingChange, 0, 0);
            tray.Context.Window.Dispatch(ref accent);
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardLookReappliesForTest > before, "A settings change did not reach the open card.");
            tray.PumpUntilIdle();
            Assert.AreEqual(before + 1, tray.Context.WidgetCardLookReappliesForTest, "The settings change redrew the card once.");

            // A high-contrast switch, which arrives only as the same message: the service sees it and raises its event, and the
            // message's own handler does not draw the card a second time on top of it.
            before = tray.Context.WidgetCardLookReappliesForTest;
            highContrast = true;
            var contrast = Message.Create(tray.Context.Window.Handle, WmSettingChange, 0, 0);
            tray.Context.Window.Dispatch(ref contrast);
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardLookReappliesForTest > before, "A high-contrast change did not reach the open card.");
            tray.PumpUntilIdle();
            Assert.AreEqual(before + 1, tray.Context.WidgetCardLookReappliesForTest, "A look change that arrives as a settings message is drawn once, not twice.");
        });
    }

    // ----- keyboard -----

    [TestMethod]
    public void ACardOpenedFromTheTrayIconByTheKeyboardShowsItsFocusVisualFromTheStartAndOneOpenedByAClickDoesNot()
    {
        Phase5.CardDesktop.Run(() =>
        {
            MouseButtons down = MouseButtons.None;
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(leftClickConnects: false), mouseButtonsDown: () => down);
            tray.PumpUntilIdle();

            // Enter on the icon: a left button down and up with no button physically down.
            var keyboard = new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0);
            tray.Context.OnIconMouseDownForKeyboard(this, keyboard);
            tray.Context.OnIconMouseClick(this, keyboard);
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardIsShownForTest, "The card never opened from the keyboard.");
            Assert.IsTrue(tray.Context.WidgetCardFocusCueVisibleForTest, "A keyboard open shows the focus visual from the start.");

            tray.Context.RequestWidgetCardForTest();
            tray.PumpUntilIdle();
            TrayHarness.PumpUntil(() => !tray.Context.WidgetCardIsShownForTest, "The card did not close on the second request.");

            // A real click: the left button is down at the button-down message.
            down = MouseButtons.Left;
            tray.Context.OnIconMouseDownForKeyboard(this, keyboard);
            tray.Context.OnIconMouseClick(this, keyboard);
            TrayHarness.PumpUntil(() => tray.Context.WidgetCardIsShownForTest, "The card never opened from a click.");
            Assert.IsFalse(tray.Context.WidgetCardFocusCueVisibleForTest, "A click opens it with no focus visual.");
        });
    }

    // ----- motion -----

    [TestMethod]
    public void BothCardsTheTrayMakesAreGivenMotionWhenTheTrayHasAnAnimationSetting()
    {
        Phase5.CardDesktop.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), taskbarWatcherPollIntervalMs: 30, settings: WidgetOn(), cardAnimationsEnabled: static () => true,
                cardEnvironmentFactory: static () => new Earshot.Tests.Phase5.FakeCardEnvironment());
            tray.PumpUntilIdle();

            // The case-open notice first: it refuses to open over the owner's own card.
            tray.Context.RaiseCaseOpenedForTest();
            TrayHarness.PumpUntil(() => tray.Context.WidgetCaseOpenCardHasMotionForTest is not null, "The case-open card was never built.");
            Assert.IsTrue(tray.Context.WidgetCaseOpenCardHasMotionForTest, "The case-open card slides and fades.");

            OpenCard(tray);
            Assert.IsTrue(tray.Context.WidgetCardHasMotionForTest, "So does the gauge's card.");
        });
    }

    [TestMethod]
    public void WithNoSettingSuppliedARealTraysCardsReadWindowsAnimationSettingAndOneWithFakeSurfacesHasNone()
    {
        var log = new CapturingLog();
        Func<bool> supplied = static () => true;

        Assert.IsNotNull(TrayContext.ChooseCardAnimations(null, surfacesAreFakes: false, log), "The production tray gives its cards motion, read from Windows.");
        Assert.IsNull(TrayContext.ChooseCardAnimations(null, surfacesAreFakes: true, log), "A tray whose surfaces are fakes has no motion.");
        Assert.AreSame(supplied, TrayContext.ChooseCardAnimations(supplied, surfacesAreFakes: true, log));
        Assert.AreSame(supplied, TrayContext.ChooseCardAnimations(supplied, surfacesAreFakes: false, log));
    }
}

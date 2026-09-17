using System.Drawing;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// The presenter against a fake desktop, card and timer. No window is created and nothing is shown.
[TestClass]
public sealed class CardPresenterTests
{
    private const string AirPodsName = "Owner\u2019s AirPods Pro";
    private static readonly CardContent Connecting = new(AirPodsName, "Connecting");
    private static readonly CardContent Connected = new(AirPodsName, "Connected");
    private static readonly TimeSpan ClickAnchorExpired = CardPresenter.ClickAnchorLifetime + TimeSpan.FromTicks(1);

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Presenter = new CardPresenter(Log, Ui.Post, Environment, CreateCard, CreateTimer, Clock);
        }

        public CapturingLog Log { get; } = new();

        public CardClock Clock { get; } = new();

        public QueuedUi Ui { get; } = new();

        public FakeCardEnvironment Environment { get; } = new();

        public List<FakeCardSurface> Cards { get; } = new();

        public List<FakeCardTimer> Timers { get; } = new();

        public CardPresenter Presenter { get; }

        public FakeCardSurface Card => Cards.Single();

        public FakeCardTimer Timer => Timers.Single();

        // The size every new fake card reports from Prepare.
        public Size CardSize { get; set; } = new(300, 80);

        public void Dispose() => Presenter.Dispose();

        private FakeCardSurface CreateCard()
        {
            var card = new FakeCardSurface { PreparedSize = CardSize };
            Cards.Add(card);
            return card;
        }

        private FakeCardTimer CreateTimer()
        {
            var timer = new FakeCardTimer();
            Timers.Add(timer);
            return timer;
        }
    }

    [TestMethod]
    public void ShowAndHideOnlyQueueWorkForTheUiThread()
    {
        using var h = new Harness();

        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Presenter.Hide();

        Assert.AreEqual(2, h.Ui.Pending);
        Assert.IsEmpty(h.Cards, "The card is created lazily, on the UI thread.");
        Assert.IsEmpty(h.Environment.ThreadIds);

        h.Ui.RunAll();

        Assert.HasCount(1, h.Card.ShownAt);
        Assert.AreEqual(1, h.Card.Hides, "Hide ran after Show, in call order.");
    }

    [TestMethod]
    public void ShowAsyncReportsWhetherTheCardWentOnScreen()
    {
        using var h = new Harness();

        Task<bool> atClick = h.Presenter.ShowAsync(Connected, CardAnchor.NearCursor, new Point(960, 1056));
        Assert.IsFalse(atClick.IsCompleted, "It completes on the UI thread, once the card is shown.");
        h.Ui.RunAll();
        Assert.IsTrue(atClick.IsCompletedSuccessfully && atClick.GetAwaiter().GetResult());

        // Quiet time holds back a card nobody clicked for.
        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_QUIET_TIME);
        Task<bool> heldBack = h.Presenter.ShowAsync(Connected, CardAnchor.NearTray, null);
        h.Ui.RunAll();
        Assert.IsFalse(heldBack.GetAwaiter().GetResult());

        // A card that cannot be put on screen is not shown either.
        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS);
        h.Card.ShowResult = StepOutcomes.FromWin32("set-window-pos:show-card", 1400);
        Task<bool> failed = h.Presenter.ShowAsync(Connected, CardAnchor.NearTray, null);
        h.Ui.RunAll();
        Assert.IsFalse(failed.GetAwaiter().GetResult());

        h.Presenter.Dispose();
        Task<bool> closed = h.Presenter.ShowAsync(Connected, CardAnchor.NearCursor, null);
        h.Ui.RunAll();
        Assert.IsFalse(closed.GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CallsFromAnotherThreadRunOnTheUiThread()
    {
        using var h = new Harness();
        int uiThread = Environment.CurrentManagedThreadId;

        Task.Run(() =>
        {
            h.Presenter.Show(Connecting, CardAnchor.NearCursor);
            h.Presenter.Hide();
        }).GetAwaiter().GetResult();
        h.Ui.RunAll();

        Assert.IsTrue(h.Card.ThreadIds.All(id => id == uiThread));
        Assert.IsTrue(h.Environment.ThreadIds.All(id => id == uiThread));
        Assert.IsNotEmpty(h.Card.ThreadIds);
    }

    [TestMethod]
    public void ANewerShowReplacesTheContentAndRestartsTheTimer()
    {
        using var h = new Harness();

        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        CollectionAssert.AreEqual(new[] { CardPresenter.DismissAfter }, h.Timer.Restarts);

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.HasCount(1, h.Cards, "One card instance is reused.");
        Assert.HasCount(1, h.Timers);
        CollectionAssert.AreEqual(new[] { Connecting, Connected }, h.Card.Prepared.Select(p => p.Content).ToArray());
        Assert.HasCount(2, h.Card.ShownAt);
        Assert.AreEqual(0, h.Card.Hides, "The card is replaced in place, not hidden and shown again.");
        CollectionAssert.AreEqual(new[] { CardPresenter.DismissAfter, CardPresenter.DismissAfter }, h.Timer.Restarts);
        Assert.IsTrue(h.Timer.Running);

        h.Timer.Elapse();

        Assert.AreEqual(1, h.Card.Hides);
        Assert.IsFalse(h.Card.OnScreen);
    }

    [TestMethod]
    public void TheCardDismissesAfterAboutFourSeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(4), CardPresenter.DismissAfter);
    }

    [TestMethod]
    public void AClickDismissesTheCard()
    {
        using var h = new Harness();
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        h.Card.Click();

        Assert.AreEqual(1, h.Card.Hides);
        Assert.IsFalse(h.Timer.Running);
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "Card dismissed by a click."));
    }

    [TestMethod]
    public void HideStopsTheTimerAndHidesTheCard()
    {
        using var h = new Harness();
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        h.Presenter.Hide();
        h.Ui.RunAll();

        Assert.AreEqual(1, h.Card.Hides);
        Assert.IsFalse(h.Timer.Running);
        Assert.IsFalse(h.Card.OnScreen);
    }

    [TestMethod]
    public void HideBeforeAnyCardCreatesNothing()
    {
        using var h = new Harness();

        h.Presenter.Hide();
        h.Ui.RunAll();

        Assert.IsEmpty(h.Cards);
        Assert.IsEmpty(h.Timers);
    }

    [TestMethod]
    [DataRow(Shell.QUNS_NOT_PRESENT, "QUNS_NOT_PRESENT")]
    [DataRow(Shell.QUNS_BUSY, "QUNS_BUSY")]
    [DataRow(Shell.QUNS_RUNNING_D3D_FULL_SCREEN, "QUNS_RUNNING_D3D_FULL_SCREEN")]
    [DataRow(Shell.QUNS_PRESENTATION_MODE, "QUNS_PRESENTATION_MODE")]
    [DataRow(Shell.QUNS_QUIET_TIME, "QUNS_QUIET_TIME")]
    [DataRow(42, "QUNS 42")]
    public void ACardNobodyClickedForIsSkippedWhileNotificationsAreNotAccepted(int state, string name)
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(0, state);

        h.Presenter.Show(Connected, CardAnchor.NearTray);
        h.Ui.RunAll();

        Assert.AreEqual(1, h.Environment.NotificationQueries);
        Assert.IsEmpty(h.Cards, "Nothing is created or shown.");
        Assert.IsEmpty(h.Timers);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Not shown, Windows is not taking notifications now (" + name + ")"));
    }

    [TestMethod]
    [DataRow(Shell.QUNS_ACCEPTS_NOTIFICATIONS)]
    [DataRow(Shell.QUNS_APP)]
    public void ACardNobodyClickedForShowsWhileNotificationsAreAccepted(int state)
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(0, state);

        h.Presenter.Show(Connected, CardAnchor.NearTray);
        h.Ui.RunAll();

        Assert.HasCount(1, h.Card.ShownAt);
        Assert.IsTrue(h.Timer.Running);
    }

    [TestMethod]
    public void ACardNobodyClickedForIsSkippedWhenTheNotificationStateCannotBeRead()
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(unchecked((int)0x80004005), 0);

        h.Presenter.Show(Connected, CardAnchor.NearTray);
        h.Ui.RunAll();

        Assert.IsEmpty(h.Cards);
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "sh-query-user-notification-state failed E_FAIL"));
    }

    [TestMethod]
    public void AnUnknownAnchorIsTreatedAsACardNobodyClickedFor()
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY);

        h.Presenter.Show(Connected, (CardAnchor)99);
        h.Ui.RunAll();

        Assert.AreEqual(1, h.Environment.NotificationQueries);
        Assert.IsEmpty(h.Cards);
    }

    [TestMethod]
    public void ACardAfterAClickAlwaysShows()
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_RUNNING_D3D_FULL_SCREEN);

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.AreEqual(0, h.Environment.NotificationQueries, "A click is the user's own action, so the state is not asked.");
        Assert.HasCount(1, h.Card.ShownAt);
    }

    [TestMethod]
    public void AResultCardLandsWhereTheCardForTheSameClickDidAfterTheCursorMoved()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(960, 1056));

        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        h.Timer.Elapse();

        // The connect takes a while; meanwhile the pointer went to the top of the screen.
        h.Clock.Advance(TimeSpan.FromSeconds(15));
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        CollectionAssert.AreEqual(new[] { new Rectangle(810, 940, 300, 80), new Rectangle(810, 940, 300, 80) }, h.Card.ShownAt);
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "anchored at the cursor on the taskbar (960,1056): NearCursor card \"" + AirPodsName + ": Connecting\""));
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "anchored where the last card after a click was (960,1056): NearCursor card \"" + AirPodsName + ": Connected\""));
    }

    [TestMethod]
    public void AReplacedCardDoesNotFollowTheCursorAlongTheTaskbar()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(1800, 1056));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(100, 1056));
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        CollectionAssert.AreEqual(new[] { new Rectangle(1608, 940, 300, 80), new Rectangle(1608, 940, 300, 80) }, h.Card.ShownAt);
    }

    [TestMethod]
    public void ACardAfterAClickWithTheCursorAwayFromTheTaskbarGoesNearTheNotificationArea()
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_BUSY);
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));

        h.Presenter.Show(new CardContent(AirPodsName, "Disconnected"), CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), h.Card.ShownAt.Single());
        Assert.AreEqual(0, h.Environment.NotificationQueries, "It still follows a click, so the notification state is not asked.");
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "near the notification area, because the cursor is away from the taskbar (750,0)"));

        // A menu or flyout sits just above the taskbar, which is not on it either.
        h.Environment.Scene = Desktops.TwoDisplays(new Point(3700, 1000));
        h.Clock.Advance(ClickAnchorExpired);
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), h.Card.ShownAt.Last(), "The corner is on the display with the notification area.");
    }

    [TestMethod]
    public void TheLastClickPointIsForgottenAfterItsLifetime()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(960, 1056));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();

        // Still within the lifetime: reused.
        h.Clock.Advance(CardPresenter.ClickAnchorLifetime);
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(810, 940, 300, 80), h.Card.ShownAt.Last());

        // Each card shown there starts the lifetime again; past it, the point is not reused.
        h.Clock.Advance(ClickAnchorExpired);
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), h.Card.ShownAt.Last());

        // A cursor on the taskbar is used again and becomes the new point.
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(300, 1056));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(150, 940, 300, 80), h.Card.ShownAt.Last());
    }

    // Callers hold the presenter as ICardPresenter, so the click point must reach it through the interface, not
    // fall back to the interface's default that drops it.
    [TestMethod]
    public void AClickPointGivenThroughTheInterfaceAnchorsTheCard()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
        ICardPresenter cards = h.Presenter;

        cards.Show(Connected, CardAnchor.NearCursor, new Point(960, 1056));
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(810, 940, 300, 80), h.Card.ShownAt.Single());
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "anchored at the click (960,1056)"));
    }

    [TestMethod]
    public void AClickPointFromTheCallerAnchorsTheCardWhereverTheCursorIsNow()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));

        h.Presenter.Show(Connected, CardAnchor.NearCursor, new Point(960, 1056));
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(810, 940, 300, 80), h.Card.ShownAt.Single());
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "anchored at the click (960,1056)"));

        // The click point also anchors a later card after a click that has none.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(810, 940, 300, 80), h.Card.ShownAt.Last());

        // A newer click point wins over the remembered one.
        h.Presenter.Show(Connected, CardAnchor.NearCursor, new Point(1800, 1056));
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), h.Card.ShownAt.Last());
    }

    [TestMethod]
    public void AClickPointOffEveryDisplayIsNotUsed()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(960, 1056));

        h.Presenter.Show(Connected, CardAnchor.NearCursor, new Point(5000, 5000));
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(810, 940, 300, 80), h.Card.ShownAt.Single(), "The cursor on the taskbar is used instead.");
    }

    [TestMethod]
    public void ACardNobodyClickedForIgnoresAClickPointAndIsStillChecked()
    {
        using var h = new Harness();
        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_QUIET_TIME);

        h.Presenter.Show(Connected, CardAnchor.NearTray, new Point(960, 1056));
        h.Ui.RunAll();
        Assert.IsEmpty(h.Cards);

        h.Environment.Notifications = new NotificationStateReading(0, Shell.QUNS_ACCEPTS_NOTIFICATIONS);
        h.Presenter.Show(Connected, CardAnchor.NearTray, new Point(960, 1056));
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), h.Card.ShownAt.Single());

        // Nor does it become the point for the next card after a click.
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "because the cursor is away from the taskbar"));
    }

    [TestMethod]
    public void ACardThatFailedToShowDoesNotBecomeTheClickPoint()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(960, 1056));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        h.Card.ShowResult = StepOutcomes.FromWin32("set-window-pos:show-card", 5);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(1800, 1056));
        h.Presenter.Show(Connected, CardAnchor.NearCursor, new Point(1800, 1056));
        h.Ui.RunAll();

        h.Card.ShowResult = StepOutcomes.FromWin32("set-window-pos:show-card", 0);
        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(810, 940, 300, 80), h.Card.ShownAt.Last(), "The last card that did show was anchored at 960,1056.");
    }

    [TestMethod]
    public void AClickPointOnADisplayThatWentAwayIsNotReused()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.TwoDisplays(new Point(3700, 1056));
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.AreEqual(new Rectangle(3528, 940, 300, 80), h.Card.ShownAt.Last());

        h.Environment.Scene = Desktops.BottomTaskbar(new Point(750, 0));
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.AreEqual(new Rectangle(1608, 940, 300, 80), h.Card.ShownAt.Last());
    }

    [TestMethod]
    public void EachShowIsLoggedAtDebug()
    {
        using var h = new Harness();

        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Presenter.Show(Connected, CardAnchor.NearTray);
        h.Ui.RunAll();

        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "Show NearCursor card \"" + AirPodsName + ": Connecting\"."));
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "Show NearTray card \"" + AirPodsName + ": Connected\"."));
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "Shown at 1608,940 300x80, 96 DPI, taskbar Bottom"));
    }

    [TestMethod]
    public void TheCardIsSizedAndPlacedForTheTargetDisplay()
    {
        using var h = new Harness();
        h.Environment.Scene = Desktops.TwoDisplays(new Point(3700, 1056));
        h.Environment.Dpi = 144;
        h.CardSize = new Size(450, 120);

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.AreEqual(Desktops.Secondary, h.Environment.DpiRequests.Last().Bounds, "The DPI is read for the display under the cursor.");
        (CardContent _, int dpi, CardPalette palette, int maxWidth) = h.Card.Prepared.Last();
        Assert.AreEqual(144, dpi);
        Assert.AreEqual(CardTheme.Dark, palette);
        Assert.AreEqual(1920 - 36, maxWidth);
        Assert.AreEqual(new Rectangle(3372, 894, 450, 120), h.Card.ShownAt.Last());
    }

    [TestMethod]
    public void AFailedShowIsLoggedWithItsCodeAndStartsNoTimer()
    {
        using var h = new Harness();
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        h.Timer.Stop();
        h.Card.ShowResult = StepOutcomes.FromWin32("set-window-pos:show-card", 5);

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.HasCount(1, h.Timer.Restarts, "Only the first, successful card started the timer.");
        Assert.IsFalse(h.Timer.Running);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "set-window-pos:show-card failed ERROR_ACCESS_DENIED"));
        Assert.IsFalse(h.Card.OnScreen);
    }

    // A card that cannot be drawn must not reach the message loop, whatever the failure was: the tray answers
    // an unhandled error with another card, through this same path. System.Drawing raises several types for
    // GDI+ statuses, OutOfMemoryException among them.
    // https://learn.microsoft.com/en-us/dotnet/api/system.drawing.image.fromfile
    [TestMethod]
    public void ACardThatCannotBeDrawnIsLoggedAndDropped()
    {
        using var h = new Harness();
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        h.Card.PrepareFailure = new IOException("The font could not be read.");

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.HasCount(1, h.Card.ShownAt, "The card that could not be drawn was never shown.");
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "Not shown: NearCursor card"));

        // The next card still works.
        h.Card.PrepareFailure = null;
        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.HasCount(2, h.Card.ShownAt);
    }

    [TestMethod]
    public void ACardClosedFromOutsideIsReplaced()
    {
        using var h = new Harness();
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();
        h.Cards[0].IsDisposed = true;

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.HasCount(2, h.Cards);
        Assert.HasCount(1, h.Cards[1].ShownAt);

        // The closed card's click no longer reaches the presenter.
        h.Cards[0].Click();
        Assert.AreEqual(0, h.Cards[1].Hides);
    }

    [TestMethod]
    public void DisposeClosesTheCardAndTimerAndLaterWorkDoesNothing()
    {
        using var h = new Harness();
        h.Presenter.Show(Connecting, CardAnchor.NearCursor);
        h.Ui.RunAll();

        h.Presenter.Show(Connected, CardAnchor.NearCursor);
        h.Presenter.Dispose();
        h.Ui.RunAll();

        Assert.IsTrue(h.Card.IsDisposed);
        Assert.IsTrue(h.Timer.IsDisposed);
        Assert.HasCount(1, h.Card.ShownAt, "The show queued before Dispose did nothing.");
        Assert.HasCount(1, h.Cards);
        Assert.IsTrue(h.Log.Has(LogLevel.Debug, "Not shown, the card is closed"));
    }

    [TestMethod]
    public void TheCompositionWiresThePopupCard()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();

        ServiceRegistry registry = CompositionRoot.Build(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: true);
        try
        {
            Assert.IsInstanceOfType<CardPresenter>(registry.Cards);
        }
        finally
        {
            ((IDisposable)registry.Cards).Dispose();
            (((SafeBlockController)registry.Block).Inner as IDisposable)?.Dispose();
            registry.Monitor.Dispose();
            registry.Worker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

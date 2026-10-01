using System.Drawing;
using Earshot.Contracts;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// A card is held for a moment before it first appears, so a status that is replaced at once is never drawn: the owner's log
// showed "Connecting" shown and "Allowing" shown 43 ms later over the same place, which is a flash of the first for nothing.
// A card that is already on screen is not held (StatusCardInPlaceTests draws its new status in place), and a notice that reports
// whether it was seen is not held either.
[TestClass]
public sealed class StatusCardHoldTests
{
    private const string Device = "Jonathan\u2019s AirPods Pro";

    // ---- A status that is replaced at once is never drawn ----

    private sealed class Held
    {
        public Held(TimeSpan? settle)
        {
            Presenter = new CardPresenter(Log, Ui.Post, new FakeCardEnvironment(), () => Card, () => new FakeCardTimer(), Time, settle);
        }

        public CapturingLog Log { get; } = new();

        public Streaming.TestTimeProvider Time { get; } = new();

        public QueuedUi Ui { get; } = new();

        public FakeCardSurface Card { get; } = new();

        public CardPresenter Presenter { get; }

        public List<string> Drawn => [.. Card.Prepared.Select(p => p.Content.Status)];
    }

    [TestMethod]
    public void AStatusReplacedWithinTheHoldIsNeverPutOnScreenAndTheNewestIsShownOnce()
    {
        var h = new Held(CardPresenter.SettleDelay);
        var click = new Point(960, 1056);

        h.Presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor, click);
        h.Ui.RunAll();
        h.Time.Advance(TimeSpan.FromMilliseconds(43));
        h.Presenter.Show(new CardContent(Device, "Allowing"), CardAnchor.NearCursor, click);
        h.Ui.RunAll();

        Assert.IsEmpty(h.Card.ShownAt, "Held: neither status is on screen yet.");

        h.Time.Advance(CardPresenter.SettleDelay);
        h.Ui.RunAll();

        Assert.HasCount(1, h.Card.ShownAt, "Shown once.");
        Assert.HasCount(1, h.Drawn, "Only one status was ever laid out, so the first was never drawn.");
        Assert.AreEqual("Allowing", h.Drawn[0]);
    }

    [TestMethod]
    public void ACardIsShownAtTheEndOfTheHoldMeasuredFromTheFirstStatusNotExtendedByTheNext()
    {
        var h = new Held(CardPresenter.SettleDelay);
        var click = new Point(960, 1056);
        h.Presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor, click);
        h.Ui.RunAll();
        h.Time.Advance(CardPresenter.SettleDelay - TimeSpan.FromMilliseconds(10));
        h.Presenter.Show(new CardContent(Device, "Allowing"), CardAnchor.NearCursor, click);
        h.Ui.RunAll();
        Assert.IsEmpty(h.Card.ShownAt);

        h.Time.Advance(TimeSpan.FromMilliseconds(10));
        h.Ui.RunAll();

        Assert.HasCount(1, h.Card.ShownAt, "A steady stream of statuses cannot keep the card off screen.");
    }

    [TestMethod]
    public void AHideDuringTheHoldMeansTheCardNeverAppears()
    {
        var h = new Held(CardPresenter.SettleDelay);
        h.Presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor);
        h.Ui.RunAll();

        h.Presenter.Hide();
        h.Ui.RunAll();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Ui.RunAll();

        Assert.IsEmpty(h.Card.ShownAt);
    }

    [TestMethod]
    public void AStatusOnACardThatIsOnScreenGoesStraightToItWithNoHold()
    {
        var h = new Held(CardPresenter.SettleDelay);
        var click = new Point(960, 1056);
        h.Presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor, click);
        h.Ui.RunAll();
        h.Time.Advance(CardPresenter.SettleDelay);
        h.Ui.RunAll();
        Assert.HasCount(1, h.Card.ShownAt);

        h.Presenter.Show(new CardContent(Device, "Connected"), CardAnchor.NearCursor, click);
        h.Ui.RunAll();

        Assert.HasCount(2, h.Card.ShownAt, "The card is up, so a new status is drawn at once.");
        Assert.AreEqual("Connected", h.Card.Prepared[^1].Content.Status);
    }

    [TestMethod]
    public void ACardThatWentAwayIsHeldAgainBeforeItAppearsAgain()
    {
        var h = new Held(CardPresenter.SettleDelay);
        h.Presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor);
        h.Ui.RunAll();
        h.Time.Advance(CardPresenter.SettleDelay);
        h.Ui.RunAll();
        h.Presenter.Hide();
        h.Ui.RunAll();

        h.Presenter.Show(new CardContent(Device, "Connected"), CardAnchor.NearCursor);
        h.Ui.RunAll();

        Assert.HasCount(1, h.Card.ShownAt, "Held again: only the first card is on screen so far.");
    }

    // A notice that reports whether it was seen is not held: it asks, and is answered, at once.
    [TestMethod]
    public void AShowThatReportsWhetherItWasSeenIsNotHeld()
    {
        var h = new Held(CardPresenter.SettleDelay);

        Task<bool> seen = h.Presenter.ShowAsync(new CardContent(Device, "Connected"), CardAnchor.NearCursor, new Point(960, 1056));
        h.Ui.RunAll();

        Assert.IsTrue(seen.IsCompletedSuccessfully && seen.GetAwaiter().GetResult());
        Assert.HasCount(1, h.Card.ShownAt);
    }

    [TestMethod]
    public void DisposingDuringTheHoldDropsTheCardAndItsTimer()
    {
        var h = new Held(CardPresenter.SettleDelay);
        h.Presenter.Show(new CardContent(Device, "Connecting"), CardAnchor.NearCursor);
        h.Ui.RunAll();
        Assert.AreEqual(1, h.Time.LiveTimers, "The hold is a timer.");

        h.Presenter.Dispose();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Ui.RunAll();

        Assert.IsEmpty(h.Card.ShownAt);
        Assert.AreEqual(0, h.Time.LiveTimers);
    }

    [TestMethod]
    public void TheTrayBuildsItsPresenterWithTheHold()
    {
        using var presenter = new CardPresenter(new CapturingLog(), static action => action());

        Assert.AreEqual(CardPresenter.SettleDelay, presenter.Settle);
        Assert.IsGreaterThan(TimeSpan.Zero, CardPresenter.SettleDelay);
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(250), CardPresenter.SettleDelay, "Short enough to feel instant after a click.");
    }
}

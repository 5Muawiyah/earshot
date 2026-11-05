using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A notice that waited behind the case card is shown when the card goes, however the card goes: the case closing, its close button,
// or its close time passing. Every window is on a private desktop.
public sealed partial class WidgetCardUnaskedTests
{
    [TestMethod]
    public void ANoticeWaitingBehindATimedCaseCardIsShownWhenTheCloseTimePasses()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            var time = new Streaming.TestTimeProvider();
            using WidgetCardPresenter presenter = NewPresenter(
                () => new WidgetCard(log, CardOpening.Unasked), new FakeCallbacks { Snapshot = Snapshot(AirPodsWhere.NotInUse) }.Build(),
                Gate(closeSeconds: 5), new Phase5.FakeCardEnvironment(), Inline, time, log, new FakeScene());
            presenter.CaseOpened(AGauge);
            Application.DoEvents();
            CompactRequest waiting = CompactNoticePresenter.RequestFor(new CardContent("Earshot", "Waiting"), CardAnchor.NearTray, null, null);
            Assert.IsFalse(presenter.ShowCompact(waiting).GetAwaiter().GetResult(), "It waits behind the case card.");
            Assert.AreEqual(1, presenter.WaitingForTest);

            time.Advance(TimeSpan.FromSeconds(5));
            Application.DoEvents();

            Assert.AreEqual("Waiting", presenter.UnaskedCompactForTest?.Text, "It is shown after the timed case card closed.");
            Assert.AreEqual(0, presenter.WaitingForTest);
        });
    }
}

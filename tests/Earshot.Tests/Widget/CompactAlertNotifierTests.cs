using System.Drawing;
using Earshot.Contracts;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class CompactAlertNotifierTests
{
    private sealed class RecordingTarget : ICompactNoticeTarget
    {
        public List<CompactRequest> Requests { get; } = new();

        public bool Answer { get; set; } = true;

        public Task<bool> ShowCompact(CompactRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }

        public void HideCompact()
        {
        }
    }

    [TestMethod]
    public async Task ALowAlertShowsItsCompactRowNearTheTrayAndAnswersShown()
    {
        var target = new RecordingTarget();
        var cards = new CompactNoticePresenter(new CapturingLog());
        cards.SetTarget(target);
        var notifier = new CompactAlertNotifier(cards);

        StepOutcome outcome = await notifier.NotifyAsync("Earshot", "Left AirPod at 20%", CompactLine.Low("Left", 20));

        Assert.IsTrue(outcome.Ok);
        CompactRequest request = target.Requests.Single();
        Assert.AreEqual("Left 20%", request.Content.Text);
        Assert.AreEqual(FluentGlyphs.Warning, request.Content.Glyph);
        Assert.AreEqual(InkRole.Caution, request.Content.Ink);
        Assert.AreEqual(CardAnchor.NearTray, request.Anchor);
        Assert.IsNull(request.Content.Action);
    }

    [TestMethod]
    public async Task AFullyChargedAlertIsTheAccentTickAndTheWords()
    {
        var target = new RecordingTarget();
        var cards = new CompactNoticePresenter(new CapturingLog());
        cards.SetTarget(target);

        await new CompactAlertNotifier(cards).NotifyAsync("Earshot", "Left AirPod at 100%, fully charged", CompactLine.FullyCharged());

        CompactContent row = target.Requests.Single().Content;
        Assert.AreEqual("Fully charged", row.Text);
        Assert.AreEqual(FluentGlyphs.CheckMark, row.Glyph);
        Assert.AreEqual(InkRole.Accent, row.Ink);
    }

    [TestMethod]
    public async Task AHeldBackCardIsReportedHeldBack()
    {
        var target = new RecordingTarget { Answer = false };
        var cards = new CompactNoticePresenter(new CapturingLog());
        cards.SetTarget(target);

        StepOutcome outcome = await new CompactAlertNotifier(cards).NotifyAsync("Earshot", "Left AirPod at 20%", CompactLine.Low("Left", 20));

        Assert.IsFalse(outcome.Ok);
    }

    [TestMethod]
    public async Task AnAlertWithNoRowIsTheTitleAndTextAsANotice()
    {
        var target = new RecordingTarget();
        var cards = new CompactNoticePresenter(new CapturingLog());
        cards.SetTarget(target);

        await new CompactAlertNotifier(cards).NotifyAsync("Heading", "Something happened");

        CompactRequest request = target.Requests.Single();
        Assert.AreEqual("Heading: Something happened", request.Content.Text);
        Assert.AreEqual(UnaskedKind.Notice, request.Content.Kind);
    }
}

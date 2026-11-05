using Earshot.App;
using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The moved-to rule fed by the real selection code on a fake clock: every message of the paired model, a latch that makes Where
// read Elsewhere, and the three ways the latch clears. Synthetic messages only: WidgetFixtures' "no product" model and colour,
// invented sender tags.
[TestClass]
public sealed class MovedToServiceTests
{
    // ---- The verdict makes Where read Elsewhere

    [TestMethod]
    public void AMovedVerdictMakesWhereElsewhereAndRaisesOnce()
    {
        using var rig = new MovedToRig();
        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where);

        rig.LeaveAndBeHeardNear();

        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);
        Assert.HasCount(1, rig.Moved);
        Assert.AreEqual(MovedVerdictKind.Moved, rig.Moved[0].Verdict.Kind);
        Assert.IsTrue(rig.LogHas("Move: 3 messages by +3800 ms, first +900 ms, median -62 dBm, case-known 0: moved."));
    }

    // Moved to needs no link: the pair is read by model, so a leave with nothing linked is still told.
    [TestMethod]
    public void ItNeedsNoLink()
    {
        using var rig = new MovedToRig();

        rig.LeaveAndBeHeardNear();

        Assert.AreEqual(BroadcastSelectionState.Listening, rig.Service.Current.Selection);
        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);
    }

    [TestMethod]
    public void WhereStaysElsewhereWhileTheLinkedPairIsHeardToo()
    {
        using var rig = new MovedToRig();
        rig.Link();
        rig.AdvanceTo(15);

        rig.LeaveAndBeHeardNear();

        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);
        Assert.AreEqual(BroadcastSelectionState.Linked, rig.Service.Current.Selection);
    }

    // The latch runs whatever the setting says; only the compact card obeys it.
    [TestMethod]
    public void ItRunsWithTheSettingOff()
    {
        using var rig = new MovedToRig();
        rig.Settings.Update(s => s.Widget = s.Widget with { MovedToOn = false });

        rig.LeaveAndBeHeardNear();

        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);
        Assert.HasCount(1, rig.Moved);
    }

    // ---- The other verdicts show nothing

    [TestMethod]
    public void IntoTheCaseJustBeforeShowsNothing()
    {
        using var rig = new MovedToRig();
        rig.Hear(0, caseKnown: true);
        rig.Hear(3, caseKnown: true);

        rig.LeaveAndBeHeardNear();

        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where);
        Assert.IsEmpty(rig.Moved);
        Assert.IsTrue(rig.LogHas("into the case."));
    }

    [TestMethod]
    public void NotHeardShowsNothingAtTheBound()
    {
        using var rig = new MovedToRig();
        rig.LeaveByOther();
        rig.AdvanceTo(rig.Now + 9.9);
        Assert.IsFalse(rig.LogHas("not heard."), "It is not said before the bound.");

        rig.AdvanceTo(rig.Now + 0.1);

        Assert.IsTrue(rig.LogHas("Move: 0 messages by +10000 ms, case-known 0: not heard."));
        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where);
        Assert.IsEmpty(rig.Moved);
    }

    [TestMethod]
    public void AWeakPairShowsNothing()
    {
        using var rig = new MovedToRig();
        rig.LeaveByOther();
        double t = rig.Now;
        rig.Hear(t + 1, rssi: -88);
        rig.Hear(t + 3, rssi: -88);
        rig.Hear(t + 5, rssi: -88);

        rig.AdvanceTo(t + 10);

        Assert.IsTrue(rig.LogHas("median -88 dBm, case-known 0: not heard."));
        Assert.IsEmpty(rig.Moved);
    }

    [TestMethod]
    public void AnotherModelIsNotCounted()
    {
        using var rig = new MovedToRig();
        rig.LeaveByOther();
        double t = rig.Now;
        rig.Hear(t + 1, modelLow: WidgetFixtures.StrangerModelLow);
        rig.Hear(t + 3, modelLow: WidgetFixtures.StrangerModelLow);
        rig.Hear(t + 5, modelLow: WidgetFixtures.StrangerModelLow);

        rig.AdvanceTo(t + 10);

        Assert.IsTrue(rig.LogHas("Move: 0 messages by +10000 ms"));
        Assert.IsEmpty(rig.Moved);
    }

    // With a pair remembered, another colour is not the owner's even if it is the same model.
    [TestMethod]
    public void AnotherColourOfTheRememberedPairIsNotCounted()
    {
        using var rig = new MovedToRig();
        rig.Link();
        rig.AdvanceTo(15);
        rig.LeaveByOther();
        double t = rig.Now;
        rig.Hear(t + 1, colour: WidgetFixtures.StrangerColour, tag: 7);
        rig.Hear(t + 3, colour: WidgetFixtures.StrangerColour, tag: 7);
        rig.Hear(t + 5, colour: WidgetFixtures.StrangerColour, tag: 7);

        rig.AdvanceTo(t + 10);

        Assert.IsTrue(rig.LogHas("Move: 0 messages by +10000 ms"));
        Assert.IsEmpty(rig.Moved);
    }

    [TestMethod]
    public void ThisPcHavingThemAgainFirstShowsNothing()
    {
        using var rig = new MovedToRig();
        rig.ThisPc(active: true);
        rig.LeaveByOther();
        rig.ThisPc(active: false);
        double t = rig.Now;
        rig.Hear(t + 1);
        rig.ThisPc(active: true);

        rig.Hear(t + 2.5);
        rig.Hear(t + 4);

        Assert.IsTrue(rig.LogHas("back on this PC."));
        Assert.AreEqual(AirPodsWhere.ThisPc, rig.Where);
        Assert.IsEmpty(rig.Moved);
    }

    // ---- The long line

    [TestMethod]
    public void ThirtySecondsAfterTheLeaveTheLineSaysWhatItHeld()
    {
        using var rig = new MovedToRig();
        double t = rig.Now;
        rig.LeaveAndBeHeardNear();
        for (int i = 1; i <= 15; i++)
        {
            rig.Hear(t + 3.8 + (i * 1.5));
        }

        rig.AdvanceTo(t + 31);

        Assert.IsTrue(rig.LogHas("Move: at +30000 ms: 18 messages, median -62 dBm, case-known 0."));
    }

    [TestMethod]
    public void TheLongLineIsWrittenOnceEvenWithNothingHeard()
    {
        using var rig = new MovedToRig();
        rig.LeaveByOther();

        rig.AdvanceTo(rig.Now + 45);

        Assert.AreEqual(1, rig.Log.Entries.Count(e => e.Message.Contains("Move: at +30000 ms: 0 messages, case-known 0.", StringComparison.Ordinal)));
    }

    // ---- The latch clears three ways

    [TestMethod]
    public void ThisPcActiveAgainClearsIt()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();
        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);

        rig.ThisPc(active: true);

        Assert.AreEqual(AirPodsWhere.ThisPc, rig.Where);

        rig.ThisPc(active: false);

        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where, "It did not come back when this PC let them go again.");
    }

    [TestMethod]
    public void ACaseKnownMessageOfThePairClearsIt()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();

        rig.Hear(rig.Now + 1, caseKnown: true);

        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where);
    }

    [TestMethod]
    public void AnotherModelsCaseKnownMessageDoesNotClearIt()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();

        rig.Hear(rig.Now + 1, caseKnown: true, modelLow: WidgetFixtures.StrangerModelLow);

        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);
    }

    [TestMethod]
    public void TwoMinutesWithNoMessageOfThePairClearsIt()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();
        double last = rig.Now;
        var changes = new List<EventArgs>();
        rig.Service.Changed += (_, e) => changes.Add(e);

        rig.AdvanceTo(last + 119);
        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where, "119 s is not yet the limit.");

        rig.AdvanceTo(last + 120);

        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where);
        Assert.IsNotEmpty(changes, "The change was published: nothing else was happening to prompt it.");
        Assert.IsTrue(rig.LogHas("Move: no message of the pair for 120 s"));
    }

    [TestMethod]
    public void AMessageEveryMinuteKeepsItForAsLongAsTheyAreHeard()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();
        double t = rig.Now;

        for (int i = 1; i <= 6; i++)
        {
            rig.Hear(t + (i * 60));
        }

        Assert.AreEqual(AirPodsWhere.Elsewhere, rig.Where);
    }

    // ---- A different pair is a different story

    [TestMethod]
    public void ForgettingThePairDropsIt()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();

        rig.Service.ForgetPair("another pair was adopted");

        Assert.AreNotEqual(AirPodsWhere.Elsewhere, rig.Where);
    }

    // ---- The consumers that never ran for real now do

    [TestMethod]
    public void TheGaugeShowsThePhoneMark()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();

        GaugeContent content = GaugeContent.From(rig.Service.Current, rig.Clock.GetUtcNow(), GaugeDisplaySettings.Default);

        Assert.AreEqual(GaugeMode.OnOtherDevice, content.Mode);
        Assert.AreEqual("AirPods, on iPhone", content.Tooltip);
    }

    [TestMethod]
    public void TheCardSaysOnYourIPhoneAndOnAnotherDeviceWithNoLabel()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();

        Assert.AreEqual("On your iPhone", WidgetCopy.Where(rig.Where, "iPhone"));
        Assert.AreEqual("On another device", WidgetCopy.Where(rig.Where, string.Empty));
    }

    [TestMethod]
    public void SendToHasTheOtherDeviceAsTheCurrentRow()
    {
        using var rig = new MovedToRig();
        rig.LeaveAndBeHeardNear();

        SendToModel model = SendToModel.From(rig.Where, "iPhone", busy: false);

        Assert.AreEqual(FluentGlyphs.CellPhone, model.WhereGlyph);
        Assert.IsTrue(model.Rows.Single(r => r.Choice == SendToChoice.OtherDevice).Current);
        Assert.IsFalse(model.Rows.Single(r => r.Choice == SendToChoice.ThisPc).Current);
    }

    [TestMethod]
    public void TheConnectFailureReasonIsInUseOnYourIPhone()
    {
        using var rig = new MovedToRig();
        rig.Link();
        rig.AdvanceTo(15);
        rig.LeaveAndBeHeardNear();
        var hints = new BroadcastDeviceHints(() => rig.Service.Current, rig.Clock);

        Assert.AreEqual(DeviceHint.InUseElsewhere, hints.Read());
    }
}

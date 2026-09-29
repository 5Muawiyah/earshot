using System.Globalization;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The lines a switch leaves in the log, byte for byte. The live test parses these, so a change here that no test
// notices would make it read nothing. The clock is the manual one: every figure is what the test advanced.
[TestClass]
public sealed class SwitchTimelineTextTests
{
    private const string Accepted = "2026-09-15T20:30:00.000Z";

    private static (SwitchTimeline Timeline, ManualTime Time) Start(bool connect, SwitchTrigger trigger = SwitchTrigger.Click)
    {
        var time = new ManualTime();
        return (new SwitchTimeline(time, new CapturingLog(), connect, trigger), time);
    }

    private static void Phase(SwitchTimeline t, ManualTime time, SwitchPhase phase, double milliseconds)
    {
        long mark = t.Mark();
        time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        t.Record(phase, mark);
    }

    [TestMethod]
    public void ToPcActiveLineIsPinnedByteForByte()
    {
        (SwitchTimeline t, ManualTime time) = Start(connect: true, SwitchTrigger.ShortcutToPc);
        time.Advance(TimeSpan.FromMilliseconds(4));
        t.CoreBegan();
        t.Path = SwitchPath.AllowFirst;
        Phase(t, time, SwitchPhase.FirstPass, 31);
        Phase(t, time, SwitchPhase.Status, 7);
        Phase(t, time, SwitchPhase.Allow, 250);
        Phase(t, time, SwitchPhase.Endpoints, 11);
        Phase(t, time, SwitchPhase.Connect, 1500);
        t.MarkActive();
        Phase(t, time, SwitchPhase.Protection, 3228);
        t.Complete(OpStatus.Success, cancelled: false);

        Assert.AreEqual(
            "Switch to-pc: active after 1803 ms (trigger shortcut-to-pc, path allow-first, queued 4, first-pass 31, status 7, allow 250, endpoints 11, connect 1500, protection 3228, total 5031, accepted " + Accepted + ").",
            SwitchTimelineText.Format(t));
        Assert.IsTrue(SwitchTimelineText.IsGood(t));
    }

    [TestMethod]
    public void APhaseThatDidNotRunReadsAsADash()
    {
        (SwitchTimeline t, ManualTime time) = Start(connect: true);
        t.CoreBegan();
        t.Path = SwitchPath.Already;
        Phase(t, time, SwitchPhase.FirstPass, 40);
        t.MarkActive();
        t.Complete(OpStatus.Success, cancelled: false);

        Assert.AreEqual(
            "Switch to-pc: active after 40 ms (trigger click, path already, queued 0, first-pass 40, status -, allow -, endpoints -, connect -, protection -, total 40, accepted " + Accepted + ").",
            SwitchTimelineText.Format(t));
    }

    [TestMethod]
    public void ToPcNotActiveLineNamesTheOutcomeAndWhetherItBlockedAgain()
    {
        foreach ((SwitchBlockedAgain again, string word) in new[]
                 {
                     (SwitchBlockedAgain.Yes, "yes"),
                     (SwitchBlockedAgain.No, "no"),
                     (SwitchBlockedAgain.NotNeeded, "not-needed"),
                 })
        {
            (SwitchTimeline t, ManualTime time) = Start(connect: true, SwitchTrigger.Menu);
            time.Advance(TimeSpan.FromMilliseconds(15012));
            t.Path = SwitchPath.AllowFirst;
            t.BlockedAgain = again;
            t.Complete(OpStatus.Failed, cancelled: false);

            Assert.AreEqual(
                "Switch to-pc: not active (outcome Failed, trigger menu, path allow-first, blocked-again " + word + ", total 15012, accepted " + Accepted + ").",
                SwitchTimelineText.Format(t));
            Assert.IsFalse(SwitchTimelineText.IsGood(t));
        }
    }

    [TestMethod]
    public void ToPcNotActiveWithNoPathReadsADash()
    {
        (SwitchTimeline t, _) = Start(connect: true);
        t.Complete(OpStatus.Failed, cancelled: false);

        StringAssert.Contains(SwitchTimelineText.Format(t), "path -,");
    }

    [TestMethod]
    public void ToPhoneReleasedAndAtRestLineIsPinnedByteForByte()
    {
        (SwitchTimeline t, ManualTime time) = Start(connect: false, SwitchTrigger.ShortcutToPhone);
        t.CoreBegan();
        time.Advance(TimeSpan.FromMilliseconds(58));
        t.MarkReleased();
        Phase(t, time, SwitchPhase.Block, 291);
        t.MarkAtRest();
        t.Complete(OpStatus.Success, cancelled: false);

        Assert.AreEqual(
            "Switch to-phone: released after 58 ms, at rest after 349 ms (trigger shortcut-to-phone, queued 0, block 291, total 349, accepted " + Accepted + ").",
            SwitchTimelineText.Format(t));
        Assert.IsTrue(SwitchTimelineText.IsGood(t));
    }

    [TestMethod]
    public void ToPhoneReleasedButNotAtRestGivesEachFixedReason()
    {
        foreach (string reason in new[]
                 {
                     SwitchTimelineText.ReasonBlockAtBootOff,
                     SwitchTimelineText.ReasonBlockDidNotTake,
                     SwitchTimelineText.ReasonStatusUnreadable,
                     SwitchTimelineText.ReasonNotSetUp,
                 })
        {
            (SwitchTimeline t, ManualTime time) = Start(connect: false, SwitchTrigger.Click);
            t.CoreBegan();
            time.Advance(TimeSpan.FromMilliseconds(9));
            t.MarkReleased();
            t.MarkNotAtRest(reason);
            t.Complete(OpStatus.Partial, cancelled: false);

            Assert.AreEqual(
                "Switch to-phone: released after 9 ms, not at rest: " + reason + " (trigger click, queued 0, block -, total 9, accepted " + Accepted + ").",
                SwitchTimelineText.Format(t));
            Assert.IsFalse(SwitchTimelineText.IsGood(t));
        }

        Assert.AreEqual(
            "Block at boot is off|the block did not take|the boot block status could not be read|not set up",
            string.Join("|", SwitchTimelineText.ReasonBlockAtBootOff, SwitchTimelineText.ReasonBlockDidNotTake, SwitchTimelineText.ReasonStatusUnreadable, SwitchTimelineText.ReasonNotSetUp));
    }

    [TestMethod]
    public void ToPhoneNotReleasedLineSaysWhetherTheMachineIsAtRest()
    {
        (SwitchTimeline blocked, ManualTime time) = Start(connect: false);
        time.Advance(TimeSpan.FromMilliseconds(12000));
        blocked.MarkAtRest();
        blocked.Complete(OpStatus.Failed, cancelled: false);
        Assert.AreEqual(
            "Switch to-phone: not released (outcome Failed, at rest yes, trigger click, total 12000, accepted " + Accepted + ").",
            SwitchTimelineText.Format(blocked));

        (SwitchTimeline open, ManualTime time2) = Start(connect: false);
        time2.Advance(TimeSpan.FromMilliseconds(12000));
        open.MarkNotAtRest(SwitchTimelineText.ReasonBlockDidNotTake);
        open.Complete(OpStatus.Failed, cancelled: false);
        Assert.AreEqual(
            "Switch to-phone: not released (outcome Failed, at rest no: the block did not take, trigger click, total 12000, accepted " + Accepted + ").",
            SwitchTimelineText.Format(open));
    }

    [TestMethod]
    public void ACancelledSwitchIsOneShortLineEitherWay()
    {
        (SwitchTimeline pc, ManualTime time) = Start(connect: true, SwitchTrigger.ShortcutToggle);
        time.Advance(TimeSpan.FromMilliseconds(220));
        pc.Complete(OpStatus.Failed, cancelled: true);
        Assert.AreEqual("Switch to-pc: cancelled (trigger shortcut-toggle, total 220, accepted " + Accepted + ").", SwitchTimelineText.Format(pc));
        Assert.IsTrue(SwitchTimelineText.IsGood(pc), "A cancellation is not a fault.");

        (SwitchTimeline phone, ManualTime time2) = Start(connect: false);
        time2.Advance(TimeSpan.FromMilliseconds(5));
        phone.Complete(OpStatus.Failed, cancelled: true);
        Assert.AreEqual("Switch to-phone: cancelled (trigger click, total 5, accepted " + Accepted + ").", SwitchTimelineText.Format(phone));
    }

    // Truncated, so a figure is never rounded up to look slower, and never fractional.
    [TestMethod]
    public void FiguresAreWholeMillisecondsTruncated()
    {
        (SwitchTimeline t, ManualTime time) = Start(connect: true);
        t.CoreBegan();
        t.Path = SwitchPath.Direct;
        Phase(t, time, SwitchPhase.FirstPass, 1719.9);
        t.MarkActive();
        t.Complete(OpStatus.Success, cancelled: false);

        StringAssert.Contains(SwitchTimelineText.Format(t), "active after 1719 ms");
        StringAssert.Contains(SwitchTimelineText.Format(t), "first-pass 1719,");
    }

    // A machine set to a language that writes numbers or dates differently must write the same line.
    [TestMethod]
    public void TheLineIsTheSameUnderAnyCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            (SwitchTimeline t, ManualTime time) = Start(connect: true, SwitchTrigger.Click);
            t.CoreBegan();
            t.Path = SwitchPath.Direct;
            Phase(t, time, SwitchPhase.FirstPass, 1234567);
            t.MarkActive();
            t.Complete(OpStatus.Success, cancelled: false);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = SwitchTimelineText.Format(t);

            // This process runs with globalization invariant mode, so no real language is available. A copy of the
            // invariant culture with the separators a time or a number is written with changed is what a machine in
            // another language does to a line that leaves the culture to chance.
            var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            odd.DateTimeFormat.TimeSeparator = "_";
            odd.DateTimeFormat.DateSeparator = "~";
            odd.NumberFormat.NegativeSign = "!";
            odd.NumberFormat.NumberGroupSeparator = "'";
            CultureInfo.CurrentCulture = odd;
            Assert.AreEqual(invariant, SwitchTimelineText.Format(t));

            StringAssert.Contains(invariant, "accepted 2026-09-15T20:30:00.000Z");
            StringAssert.Contains(invariant, "active after 1234567 ms");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [TestMethod]
    public void TriggerAndPathWordsAreFixed()
    {
        Assert.AreEqual("click", SwitchTimelineText.Trigger(SwitchTrigger.Click));
        Assert.AreEqual("menu", SwitchTimelineText.Trigger(SwitchTrigger.Menu));
        Assert.AreEqual("shortcut-toggle", SwitchTimelineText.Trigger(SwitchTrigger.ShortcutToggle));
        Assert.AreEqual("shortcut-to-pc", SwitchTimelineText.Trigger(SwitchTrigger.ShortcutToPc));
        Assert.AreEqual("shortcut-to-phone", SwitchTimelineText.Trigger(SwitchTrigger.ShortcutToPhone));
        Assert.AreEqual("already", SwitchTimelineText.Path(SwitchPath.Already));
        Assert.AreEqual("direct", SwitchTimelineText.Path(SwitchPath.Direct));
        Assert.AreEqual("allow-first", SwitchTimelineText.Path(SwitchPath.AllowFirst));
        Assert.AreEqual("handsfree-assisted", SwitchTimelineText.Path(SwitchPath.HandsFreeAssisted));
        Assert.AreEqual("-", SwitchTimelineText.Path(SwitchPath.None));
    }

    // Nothing that names a device is ever part of a line: the timeline holds no such thing, and this proves it stays so.
    [TestMethod]
    public void NoLineCarriesADeviceNameAddressOrContainer()
    {
        (SwitchTimeline t, ManualTime time) = Start(connect: true);
        t.CoreBegan();
        t.Path = SwitchPath.AllowFirst;
        Phase(t, time, SwitchPhase.Allow, 250);
        t.MarkActive();
        t.Complete(OpStatus.Success, cancelled: false);

        string line = SwitchTimelineText.Format(t);
        StringAssert.DoesNotMatch(line, new System.Text.RegularExpressions.Regex("[0-9A-Fa-f]{12}(?![0-9A-Fa-f])"));
        Assert.IsFalse(line.Contains(Devices.Name, StringComparison.Ordinal));
        Assert.IsFalse(line.Contains(Devices.Address, StringComparison.Ordinal));
        Assert.IsFalse(line.Contains(Devices.Container.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(line.Contains("AirPods", StringComparison.OrdinalIgnoreCase));
    }

    // The at-rest action must survive a measurement that cannot be built: the tray drops the line, says why in
    // one warning, and never throws.
    [TestMethod]
    public void ALineThatCannotBeBuiltIsDroppedWithOneWarningAndNoException()
    {
        (SwitchTimeline t, _) = Start(connect: true);
        t.Path = (SwitchPath)99;
        t.MarkActive();
        t.Complete(OpStatus.Success, cancelled: false);
        var log = new CapturingLog();
        var report = new ToggleReport(true, OpStatus.Success, "", Array.Empty<StepOutcome>(), Cancelled: false) { Timeline = t };

        TrayContext.LogSwitch(log, report);

        Assert.HasCount(1, log.Entries);
        Assert.AreEqual(LogLevel.Warn, log.Entries[0].Level);
        StringAssert.Contains(log.Entries[0].Message, "ArgumentOutOfRangeException");
    }

    [TestMethod]
    public void AReportWithNoTimelineWritesNothingAndThrowsNothing()
    {
        var log = new CapturingLog();

        TrayContext.LogSwitch(log, new ToggleReport(true, OpStatus.Success, "", Array.Empty<StepOutcome>(), Cancelled: false));

        Assert.IsEmpty(log.Entries);
    }

    [TestMethod]
    public void AGoodLineIsInfoAndAFaultIsWarn()
    {
        (SwitchTimeline good, _) = Start(connect: true);
        good.MarkActive();
        good.Complete(OpStatus.Success, cancelled: false);
        (SwitchTimeline bad, _) = Start(connect: true);
        bad.Complete(OpStatus.Failed, cancelled: false);
        var log = new CapturingLog();

        TrayContext.LogSwitch(log, new ToggleReport(true, OpStatus.Success, "", Array.Empty<StepOutcome>(), Cancelled: false) { Timeline = good });
        TrayContext.LogSwitch(log, new ToggleReport(true, OpStatus.Failed, "", Array.Empty<StepOutcome>(), Cancelled: false) { Timeline = bad });

        Assert.AreEqual(LogLevel.Info, log.Entries[0].Level);
        Assert.AreEqual(LogLevel.Warn, log.Entries[1].Level);
    }
}

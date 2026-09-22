using System.Globalization;
using Earshot.App;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// T15: every shape byte for byte, invariant culture under a non-English culture, and no device name, address
// or container id in any of them. The live-test scripts (test 17, test 18) parse exactly this text, so a
// change here is a format change they must be told about too.
[TestClass]
public sealed class HandBackTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 1, 2, 3, 456, TimeSpan.Zero);

    [TestMethod]
    public void OffNamesWhichTrigger()
    {
        Assert.AreEqual("Hand-back: off, so nothing runs for this session end.", HandBackText.Off(HandBackTrigger.SessionEnd));
        Assert.AreEqual("Hand-back: off, so nothing runs for this suspend.", HandBackText.Off(HandBackTrigger.Suspend));
    }

    [TestMethod]
    public void StartedNamesEveryField()
    {
        string line = HandBackText.Started(HandBackTrigger.SessionEnd, T0, "WM_ENDSESSION, shutdown or restart", RenderState.Active, BlockState.Allowed, streamingHeld: true, blockAtBoot: true);

        Assert.AreEqual(
            "Hand-back (shutdown): started at 2026-09-22T01:02:03.456Z (WM_ENDSESSION, shutdown or restart); render Active; nodes Allowed; streaming held; Block at boot on",
            line);
    }

    [TestMethod]
    public void StartedShowsBlockAtBootUnknownWhenItIsNull()
    {
        string line = HandBackText.Started(HandBackTrigger.Suspend, T0, "PBT_APMSUSPEND", RenderState.Unknown, BlockState.Unknown, streamingHeld: false, blockAtBoot: null);

        Assert.IsTrue(line.EndsWith("Block at boot unknown", StringComparison.Ordinal));
        Assert.IsTrue(line.Contains("streaming none", StringComparison.Ordinal));
        Assert.StartsWith("Hand-back (sleep): ", line);
    }

    [TestMethod]
    public void NothingToDisconnectNamesTheTrigger()
    {
        Assert.AreEqual("Hand-back (shutdown): nothing to disconnect", HandBackText.NothingToDisconnect(HandBackTrigger.SessionEnd));
        Assert.AreEqual("Hand-back (sleep): nothing to disconnect", HandBackText.NothingToDisconnect(HandBackTrigger.Suspend));
    }

    [TestMethod]
    public void DisconnectConfirmedNamesTheOutcomeAndElapsed()
    {
        string line = HandBackText.Disconnect(HandBackTrigger.SessionEnd, "S_OK", confirmed: true, TimeSpan.FromMilliseconds(37));

        Assert.AreEqual("Hand-back (shutdown): disconnect S_OK, confirmed after 37 ms", line);
    }

    [TestMethod]
    public void DisconnectNotConfirmedSaysSo()
    {
        string line = HandBackText.Disconnect(HandBackTrigger.Suspend, "S_OK", confirmed: false, TimeSpan.FromMilliseconds(750));

        Assert.AreEqual("Hand-back (sleep): disconnect S_OK, not confirmed within 750 ms", line);
    }

    [TestMethod]
    public void BlockSentAtNamesTheTime()
    {
        Assert.AreEqual("Hand-back (shutdown): block sent at 2026-09-22T01:02:03.456Z", HandBackText.BlockSentAt(HandBackTrigger.SessionEnd, T0));
    }

    [TestMethod]
    public void BlockNotSentNamesTheReason()
    {
        Assert.AreEqual("Hand-back (shutdown): block not sent: Block at boot is off", HandBackText.BlockNotSent(HandBackTrigger.SessionEnd, "Block at boot is off"));
    }

    [TestMethod]
    public void FinishedNamesEverything()
    {
        string line = HandBackText.Finished(HandBackTrigger.SessionEnd, TimeSpan.FromMilliseconds(303), "confirmed", "Success");

        Assert.AreEqual("Hand-back (shutdown): finished in 303 ms; disconnect confirmed; block Success", line);
    }

    [TestMethod]
    public void CutShortNamesWhatIsStillRunningAndWhetherTheBlockWasSent()
    {
        string line = HandBackText.CutShort(HandBackTrigger.SessionEnd, TimeSpan.FromMilliseconds(4000), ["block"], T0);

        Assert.AreEqual("Hand-back (shutdown): cut short at 4000 ms; still running: block; block was sent at 2026-09-22T01:02:03.456Z", line);
    }

    [TestMethod]
    public void CutShortUsesADashForNothingStillRunningAndForNoBlockSent()
    {
        string line = HandBackText.CutShort(HandBackTrigger.Suspend, TimeSpan.FromMilliseconds(1500), [], blockSentAt: null);

        Assert.AreEqual("Hand-back (sleep): cut short at 1500 ms; still running: -; block was not sent", line);
    }

    [TestMethod]
    public void SessionEndingNoBlockAtQueryIsExact()
    {
        Assert.AreEqual(
            "Session ending: no block issued at the query, because the AirPods are in use and Hand back is on; " +
            "the hand-back runs when Windows confirms the session is ending.",
            HandBackText.SessionEndingNoBlockAtQuery());
    }

    [TestMethod]
    public void ResumeLinesAreExact()
    {
        Assert.AreEqual("Hand-back (resume): connected at resume; the resume check did not hold", HandBackText.ResumeConnected());
        Assert.AreEqual("Hand-back (resume): the nodes were enabled and not in use, so they are blocked now", HandBackText.ResumeBlocked());
        Assert.AreEqual("Hand-back (resume): nothing to do: Block at boot is off", HandBackText.ResumeNothingToDo("Block at boot is off"));
    }

    // Every number in every line must read the same whatever the current culture is: a build run under a
    // culture with a different decimal separator or calendar must not let either leak into the log, since
    // tests 17 and 18 parse this text on whatever locale the owner's PC has. This process runs with
    // globalization-invariant mode on (Directory.Build.props), so a named culture such as "de-DE" cannot be
    // looked up; a clone of the invariant culture with its number format changed proves the same thing without
    // needing culture data this build does not carry.
    [TestMethod]
    public void EveryLineIsInvariantUnderADifferentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            var different = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            different.NumberFormat.NumberDecimalSeparator = ",";
            different.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = different;

            string started = HandBackText.Started(HandBackTrigger.SessionEnd, T0, "x", RenderState.Active, BlockState.Blocked, true, true);
            string disconnect = HandBackText.Disconnect(HandBackTrigger.SessionEnd, "S_OK", true, TimeSpan.FromMilliseconds(1234.5));
            string finished = HandBackText.Finished(HandBackTrigger.SessionEnd, TimeSpan.FromMilliseconds(1234.5), "confirmed", "Success");

            Assert.Contains("2026-09-22T01:02:03.456Z", started);
            Assert.Contains("1234 ms", disconnect);
            Assert.Contains("1234 ms", finished);
            Assert.IsFalse(started.Contains(',', StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Nothing here can ever carry the pinned address, the container id or a device name: none of the formatter's
    // parameters accept one, so this is a compile-time guarantee, checked here by grepping the produced text for
    // the shapes a device identity takes (12 hex, or a GUID) never turning up by accident.
    [TestMethod]
    public void NoLineContainsSomethingThatLooksLikeADeviceIdentity()
    {
        string[] lines =
        [
            HandBackText.Started(HandBackTrigger.SessionEnd, T0, "x", RenderState.Active, BlockState.Blocked, true, true),
            HandBackText.Disconnect(HandBackTrigger.SessionEnd, "S_OK", true, TimeSpan.Zero),
            HandBackText.Finished(HandBackTrigger.SessionEnd, TimeSpan.Zero, "confirmed", "Success"),
        ];

        foreach (string line in lines)
        {
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(line, "[0-9A-Fa-f]{12}"), line);
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(line, "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}"), line);
        }
    }
}

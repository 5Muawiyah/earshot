using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Repair;
using Earshot.Tests.Pairing;
using Earshot.Tests.Phase4;
using Earshot.Tests.Phase6;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// Fix connection's run as the owner's rules shape it: two explicit on-screen questions however empty the plan, the radio read
// before anything is removed, and one history entry for the pairing.
[TestClass]
public sealed class FixConnectionTests
{
    private static async Task<FixResult> RunAsync(FixRig rig) => await FixConnection.RunAsync([], rig.Host(), CancellationToken.None);

    private static ConnectHistory HistoryIn(TempFolder temp, ManualTime time) =>
        new(Path.Combine(temp.Path, "connect-history.jsonl"), time, new CapturingLog());

    [TestMethod]
    public async Task AnEmptyPlanNeedsTwoOnScreenAnswersBeforeAnyPairing()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        // The first answer alone starts nothing: the second question was asked, got no OK, and nothing was removed or paired.
        Assert.HasCount(2, rig.Prompter.Asked);
        Assert.AreEqual(FixOutcome.Declined, result.Outcome);
        Assert.IsEmpty(rig.Pairing.Calls);
        Assert.IsEmpty(rig.Elevator.Calls);
    }

    [TestMethod]
    public async Task AnEmptyPlanDeclinedAtTheFirstQuestionAsksNothingMore()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayCancel();

        FixResult result = await RunAsync(rig);

        Assert.HasCount(1, rig.Prompter.Asked);
        Assert.AreEqual(FixOutcome.Declined, result.Outcome);
        Assert.IsEmpty(rig.Pairing.Calls);
    }

    [TestMethod]
    public async Task ThePairPressIsNotACountedQuestion()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Done, result.Outcome, string.Join("; ", result.Lines));
        Assert.HasCount(2, rig.Prompter.Asked, "The pick through the window is a step of the pairing, not a third question.");
        Assert.IsTrue(rig.Ui.Shown.Any(s => s.StartsWith("pick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ABlockingRadioIsReadBeforeAnythingIsRemovedAndEndsTheRunWithItsLine()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Radio = new FakeRadioCheck(PairingSessionTests.Radio(RadioCheckState.Off));
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        Assert.IsTrue(result.Lines.Contains("Bluetooth is off."), string.Join("; ", result.Lines));
        Assert.IsEmpty(rig.Pairing.Calls, "Nothing was listed, removed, scanned or paired.");
        Assert.IsEmpty(rig.GateFake.Calls, "Nothing was touched, so no block follows.");
        Assert.AreEqual(FixRecoveryState.None, rig.Recovery.Read().State);
    }

    [TestMethod]
    public async Task AnOkRadioDoesNotStopTheRun()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Radio = new FakeRadioCheck(PairingSessionTests.Radio(RadioCheckState.Ok));
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Done, result.Outcome, string.Join("; ", result.Lines));
        Assert.AreEqual(1, ((FakeRadioCheck)rig.Radio).Checks, "One read for the run.");
    }

    [TestMethod]
    public async Task TheRunWritesOnePairingEntryWithTheAdoptCodeAndNoDeviceName()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        var time = new ManualTime();
        ConnectHistory history = HistoryIn(temp, time);
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.GateFake.AdoptResult = _ => new FixGateResult(false, "Could not adopt", [], 8);
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        await RunAsync(rig);

        ConnectHistoryEntry entry = history.Read().Single();
        Assert.AreEqual("pair", entry.Action);
        Assert.AreEqual("paired", entry.Outcome);
        Assert.AreEqual(8, entry.Steps.Single(s => s.Name == "adopt").Code);
        Assert.IsTrue(entry.Steps.Any(s => s.Name == "pair" && s.CodeName == "Paired"));
        string raw = File.ReadAllText(history.FilePath);
        Assert.IsFalse(raw.Contains("Jonathan", StringComparison.Ordinal));
        Assert.IsFalse(raw.Contains(FixRig.NewAirPodsAddress, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task WithNothingSetUpTheAdoptStepIsCodeZero()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ConnectHistory history = HistoryIn(temp, new ManualTime());
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        File.Delete(rig.Gate.Store.DeviceFile);
        File.Delete(rig.Gate.Store.ConfigFile);
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        await RunAsync(rig);

        HistoryStep adopt = history.Read().Single().Steps.Single(s => s.Name == "adopt");
        Assert.AreEqual(0, adopt.Code);
        Assert.AreEqual("not-set-up", adopt.CodeName);
    }

    [TestMethod]
    public async Task APairingThatNeverStartedLeavesNoHistoryEntry()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ConnectHistory history = HistoryIn(temp, new ManualTime());
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();

        await RunAsync(rig);

        Assert.IsEmpty(history.Read());
    }

    // The removal is a step of the pairing too: its status and time lead the entry, and a removal Windows refused, which ends
    // the run before any scan, still leaves an entry.
    [TestMethod]
    public async Task TheRemovalLeadsTheEntryWithItsStatusAndTime()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ConnectHistory history = HistoryIn(temp, new ManualTime());
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        await RunAsync(rig);

        string[] names = history.Read().Single().Steps.Select(s => s.Name).ToArray();
        CollectionAssert.AreEqual(RemovalSteps, names.Take(2).ToArray(), string.Join(", ", names));
    }

    private static readonly string[] RemovalSteps = ["unpair", "unpair-ms"];

    [TestMethod]
    public async Task ARemovalWindowsRefusedLeavesAnEntryWithItsStatus()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ConnectHistory history = HistoryIn(temp, new ManualTime());
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Pairing.UnpairResult = new PairingCallResult("Failed", 19, false, null);
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        await RunAsync(rig);

        ConnectHistoryEntry entry = history.Read().Single();
        Assert.AreEqual("failed", entry.Outcome);
        HistoryStep unpair = entry.Steps.Single(s => s.Name == "unpair");
        Assert.AreEqual(19, unpair.Code);
        Assert.IsTrue(entry.Steps.Any(s => s.Name == "unpair-ms"));
    }

    // The pair was already removed when the person cancels in the pairing window: the run still leaves its entry, with the
    // steps taken so far and where it stopped, as Pair AirPods does.
    [TestMethod]
    public async Task ARePairCancelledInItsWindowStillLeavesACancelledEntryWithItsSteps()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ConnectHistory history = HistoryIn(temp, new ManualTime());
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();
        using var cancel = new CancellationTokenSource();
        rig.Ui.OnPick = cancel.Cancel;

        FixResult result = await FixConnection.RunAsync([], rig.Host(), cancel.Token);

        Assert.Contains("Cancelled.", result.Lines);
        ConnectHistoryEntry entry = history.Read().Single();
        Assert.AreEqual("pair", entry.Action);
        Assert.AreEqual("cancelled", entry.Outcome);
        Assert.IsTrue(entry.Steps.Any(s => s.Name == "scan"), string.Join(", ", entry.Steps.Select(s => s.Name)));
        HistoryStep stopped = entry.Steps.Single(s => s.Name == "cancelled");
        Assert.AreEqual("by-person", stopped.CodeName);
        Assert.IsTrue(entry.Steps.Any(s => s.Name == "cancelled-ms"));
    }

    [TestMethod]
    public async Task ARePairThatThrowsStillLeavesAFailedEntryWithTheCode()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ConnectHistory history = HistoryIn(temp, new ManualTime());
        rig.History = history;
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Pairing.ThrowOnPair = new IOException("the pairing call failed");
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        ConnectHistoryEntry entry = history.Read().Single();
        Assert.AreEqual("failed", entry.Outcome);
        HistoryStep stopped = entry.Steps.Single(s => s.Name == "unexpected");
        Assert.AreEqual(new IOException().HResult, stopped.Code);
        Assert.IsTrue(entry.Steps.Any(s => s.Name == "pick"), string.Join(", ", entry.Steps.Select(s => s.Name)));
    }
}

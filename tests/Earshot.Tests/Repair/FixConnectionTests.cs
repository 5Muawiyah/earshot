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
}

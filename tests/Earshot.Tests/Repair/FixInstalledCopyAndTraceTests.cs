using Earshot.App;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Pairing;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// Fix connection raises only the installed copy, and a result that points at this PC's Bluetooth offers Record a Bluetooth trace
// through the tray, which shows the same note as its settings row.
[TestClass]
public sealed class FixInstalledCopyAndTraceTests
{
    private static async Task<FixResult> RunAsync(FixRig rig) => await FixConnection.RunAsync([], rig.Host(), CancellationToken.None);

    [TestMethod]
    public void ThePromptingStepRaisesTheInstalledCopyAndNothingElse()
    {
        var launcher = new FakeLauncher();

        IFixElevator installed = FixElevationChoice.Prompting(launcher, @"C:\Program Files\Earshot\Earshot.exe");
        IFixElevator none = FixElevationChoice.Prompting(launcher, null);

        Assert.IsInstanceOfType<ShellRunasFixElevator>(installed);
        Assert.IsInstanceOfType<NotInstalledFixElevator>(none);
    }

    [TestMethod]
    public async Task WithNoInstalledCopyNothingIsRaised()
    {
        var launcher = new FakeLauncher();
        IFixElevator none = FixElevationChoice.Prompting(launcher, null);

        ElevatedRun run = await none.ApplyAsync(FixConsent.Issue(FixConsentKind.Changes), @"C:\x\plan.json", new string('a', 64), CancellationToken.None);

        Assert.IsNull(run.ExitCode);
        Assert.AreEqual(NotInstalledFixElevator.StepName, run.Step.Step);
        Assert.IsEmpty(launcher.Launches);
    }

    // Changes to make and no installed copy: nothing is asked, and the result says to set up Earshot first.
    [TestMethod]
    public async Task ChangesWithNoInstalledCopyAskNothingAndSayToSetUpFirst()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        rig.ElevatorOverride = new NotInstalledFixElevator();
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.AreEqual(FixOutcome.Failed, result.Outcome);
        CollectionAssert.Contains(result.Lines.ToList(), FixConnection.SetUpFirstNote);
        Assert.IsEmpty(rig.Prompter.Asked, "Nothing was asked.");
        Assert.IsEmpty(rig.Elevator.Calls);
        Assert.IsEmpty(rig.Pairing.Calls);
    }

    // Nothing to undo needs no administrator run, so the two questions and the pairing go on as before.
    [TestMethod]
    public async Task AnEmptyPlanWithNoInstalledCopyStillAsksBothQuestions()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.ElevatorOverride = new NotInstalledFixElevator();
        rig.Prompter.SayOk();
        rig.Prompter.SayCancel();

        FixResult result = await RunAsync(rig);

        Assert.HasCount(2, rig.Prompter.Asked);
        Assert.AreEqual(FixOutcome.Declined, result.Outcome);
    }

    // The AirPods were found with no name read and the pairing failed: the PC could not reach them.
    private static void ArrangeUnreachable(FixRig rig)
    {
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.Pairing.Events.RemoveAll(e => e.Device?.Id == FixRePairTests.NewId);
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(FixRePairTests.NewId, "", FixRig.NewAirPodsAddress, IsAudioVideo: true, MinorClass: 6)));
        rig.Pairing.PairResult = new PairingCallResult("HardwareFailure", PairReasons.HardwareFailure, false, null);
    }

    [TestMethod]
    public async Task AnUnreachablePairOffersTheTraceAndTheButtonAsksTheTray()
    {
        using var rig = new FixRig();
        ArrangeUnreachable(rig);
        int asked = 0;
        rig.RequestTrace = () =>
        {
            asked++;
            return true;
        };
        rig.Ui.PressTrace = true;
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.IsTrue(result.OfferTrace, string.Join("; ", result.Lines));
        Assert.IsTrue(rig.Ui.Shown.Any(s => s.StartsWith("result:", StringComparison.Ordinal) && s.EndsWith("[trace offered]", StringComparison.Ordinal)));
        Assert.AreEqual(1, asked, "The tray was asked once; it shows the note, and nothing is recorded from here.");
    }

    [TestMethod]
    public async Task WithNoTrayListeningTheResultSaysHowToStartATrace()
    {
        using var rig = new FixRig();
        ArrangeUnreachable(rig);
        rig.RequestTrace = () => false;
        rig.Ui.PressTrace = true;
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        await RunAsync(rig);

        CollectionAssert.Contains(rig.Ui.Shown, "result:" + FixConnection.TraceNotHeardNote);
    }

    [TestMethod]
    public async Task AResultThatDoesNotPointAtThisPcsBluetoothOffersNoTrace()
    {
        using var rig = new FixRig();
        FixRePairTests.ArrangeHappyPairing(rig);
        rig.RequestTrace = () => true;
        rig.Prompter.SayOk();
        rig.Prompter.SayOk();

        FixResult result = await RunAsync(rig);

        Assert.IsFalse(result.OfferTrace);
        Assert.IsFalse(rig.Ui.Shown.Any(s => s.EndsWith("[trace offered]", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TheRealSignalReachesAListeningTrayAndSaysWhenNoneIsListening()
    {
        string name = TraceRequestSignal.BaseName + ".test-" + Guid.NewGuid().ToString("N");
        var log = new CapturingLog();

        Assert.IsFalse(TraceRequestSignal.Raise(name, log), "No tray is listening yet.");

        using var heard = new ManualResetEventSlim();
        using TraceRequestSignal? signal = TraceRequestSignal.Listen(name, heard.Set, log);
        Assert.IsNotNull(signal);

        Assert.IsTrue(TraceRequestSignal.Raise(name, log));
        Assert.IsTrue(heard.Wait(TimeSpan.FromSeconds(10)), "The tray's callback ran.");
    }
}

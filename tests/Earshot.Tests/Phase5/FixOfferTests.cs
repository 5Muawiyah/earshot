using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Popup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// The Fix offer: one button under the failure reason, made only for a Connect that failed on this PC's side, and a card that
// carries a button, draws it, hit-tests it and runs what it does. The fix itself is not started by any test.
[TestClass]
public sealed class FixOfferTests
{
    private const string Reason = "The audio driver did not accept the request.";

    // ---- when it is offered

    [TestMethod]
    public void APcSideConnectFailureThatReachedTheThresholdIsOffered()
    {
        Assert.IsTrue(FixOffer.ShouldOffer(new FixOfferFacts(true, FixFailureClass.PcSide), succeeded: false, cancelled: false, wasConnect: true));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(1)]
    [DataRow(0)]
    public void NeverOfferedForAnyOtherClassOfFailure(int failure)
    {
        // Out of range, in the case, on the other device: the AirPods' side, so a fix on this PC cannot help, however many times.
        Assert.IsFalse(FixOffer.ShouldOffer(new FixOfferFacts(true, failure switch { 2 => FixFailureClass.DeviceSide, 1 => FixFailureClass.Temporary, _ => FixFailureClass.None }), succeeded: false, cancelled: false, wasConnect: true));
    }

    [TestMethod]
    public void NeverOfferedBeforeTheThresholdOnSuccessOnCancellationOrForADisconnect()
    {
        var due = new FixOfferFacts(true, FixFailureClass.PcSide);

        Assert.IsFalse(FixOffer.ShouldOffer(new FixOfferFacts(false, FixFailureClass.PcSide), false, false, true));
        Assert.IsFalse(FixOffer.ShouldOffer(due, succeeded: true, cancelled: false, wasConnect: true));
        Assert.IsFalse(FixOffer.ShouldOffer(due, succeeded: false, cancelled: true, wasConnect: true));
        Assert.IsFalse(FixOffer.ShouldOffer(due, succeeded: false, cancelled: false, wasConnect: false));
    }

    [TestMethod]
    public void AReportThatCarriesNoFactsOffersNothing()
    {
        var report = new ToggleReport(true, OpStatus.Failed, Reason, [], Cancelled: false);

        Assert.IsFalse(FixOffer.ShouldOffer(FixOfferFacts.From(report), report.IsSuccess, report.Cancelled, wasConnect: true));
    }

    // ---- the tray's side

    [TestMethod]
    public void TheRealLauncherNamesTheFixVerbOfThisProgram()
    {
        // Not started here: the verb it passes is the one the program dispatches, and the label is the button's.
        string label = FixOffer.ButtonLabel;
        Assert.AreEqual("Fix connection", label);
        Assert.IsInstanceOfType<IFixLauncher>(new OwnProgramFixLauncher());
    }

    // The launcher is the real one and the program is the built Earshot.exe, in safe mode and against a temporary data folder,
    // where fix prints its plan and ends with PlanOnly without asking anyone. The one real execution of the tray's launcher:
    // it starts a Windows-subsystem program with no --out and nothing redirected, as the tray does. (A test host has a console
    // its child can attach to, so this cannot by itself prove the no-console branch; CommandOutputTests and the run in
    // NoConsoleFixRunTests do.)
    [TestMethod]
    public void TheTrayStartsFixWithNoConsoleAndItRuns()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        using var runtime = new RuntimeVariablesCleared();
        using var temp = new TempFolder();
        using var ended = new ManualResetEventSlim();
        var launcher = new OwnProgramFixLauncher(() => ended.Set(), () => exe);
        using var safe = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var root = new EnvironmentVariableScope(Paths.DataRootVariable, temp.File("data"));

        Assert.IsTrue(launcher.Start(new CapturingLog()));

        Assert.IsTrue(ended.Wait(TimeSpan.FromSeconds(60)), "The fix run did not end.");
        Assert.AreEqual(ExitCodes.PlanOnly, launcher.LastExitCode, "Fix connection prints its plan and ends with PlanOnly; IoError would mean it found nowhere to write.");
        string logFile = Path.Combine(temp.File("data"), "Local", "Earshot", "logs", "earshot.log");
        string log = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
        Assert.IsFalse(log.Contains("no console to write to", StringComparison.Ordinal), log);
    }

    // Two presses, two cards and a menu item all reach one launcher, so a second run is refused while the first is open.
    [TestMethod]
    public void TheLauncherRefusesWhileItsRunIsAlive()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        using var ended = new ManualResetEventSlim();
        var log = new CapturingLog();
        var launcher = new OwnProgramFixLauncher(() => ended.Set(), () => ping, ["-n", "3", "127.0.0.1"]);
        Assert.IsFalse(launcher.IsRunning);

        Assert.IsTrue(launcher.Start(log));
        Assert.IsTrue(launcher.IsRunning);
        Assert.IsFalse(launcher.Start(log), "A second run while the first is open.");
        Assert.IsTrue(log.Has(LogLevel.Info, "still open"));

        Assert.IsTrue(ended.Wait(TimeSpan.FromSeconds(30)), "The first run did not end.");
        Assert.IsFalse(launcher.IsRunning);
        Assert.AreEqual(0, launcher.LastExitCode);
    }

    // The code read when a run ends is that run's own. Before, a new run kept the previous run's code until its own was read,
    // so a run whose code could not be read was taken for the one before it (an earlier AlreadyRunning skipped the recheck).
    [TestMethod]
    public void ANewRunDoesNotKeepThePreviousRunsExitCode()
    {
        string where = Path.Combine(Environment.SystemDirectory, "where.exe");
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        string next = where;
        using var ended = new AutoResetEvent(false);
        var log = new CapturingLog();
        var launcher = new OwnProgramFixLauncher(() => ended.Set(), () => next, ["-n", "3", "127.0.0.1"]);

        Assert.IsTrue(launcher.Start(log));
        Assert.IsTrue(ended.WaitOne(TimeSpan.FromSeconds(30)), "The first run did not end.");
        Assert.IsNotNull(launcher.LastExitCode, "where.exe refuses -n and ends with a code.");

        next = ping;
        Assert.IsTrue(launcher.Start(log));
        Assert.IsTrue(launcher.IsRunning);
        Assert.IsNull(launcher.LastExitCode, "While the second run is open, no code is its own yet.");
        Assert.IsTrue(ended.WaitOne(TimeSpan.FromSeconds(30)), "The second run did not end.");
        Assert.AreEqual(0, launcher.LastExitCode);
    }
}

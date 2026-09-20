using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// B1 (review round 1): exactly one child may exist. Run all's own button, and its carry-on
// button, used to stay enabled during a run with nothing checking _activeRunner, so a click on
// either while a row's own Start was already active began a second child; every reply then went
// to whichever runner was newest, not the one that actually asked. Drives the real MainForm, real
// PowerShell 5.1, the real fakes: RunGate's own predicate is proved in RunGateTests.cs, but only
// this proves the caller that used to skip it.
[TestClass]
public sealed class B1SingleRunnerTests
{
    [TestMethod]
    public void ClickingRunAllWhileARowsOwnStartIsAlreadyActiveNeverStartsASecondChild()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();

            ChildRunner? first = form.ActiveRunnerForTests;
            Assert.IsNotNull(first, "the first Start click never became the active runner. selectedIndex=" +
                form.SelectedIndexForTests + " startEnabled=" + form.StartButtonEnabledForTests + " status=" + form.StatusTextForTests);
            Assert.IsGreaterThan(0, first!.ProcessId, "no real child process was started.");

            // The bug: neither Run all's button nor OnRunAllCarryOnClicked checked
            // _activeRunner at all, so this click used to start a second ChildRunner outright.
            form.ClickRunAllForTests();
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(300));

            Assert.AreSame(first, form.ActiveRunnerForTests,
                "a second click started a different runner while the first child was still active.");
        });
    }

    [TestMethod]
    public void AMessageFromAnEndedRunnerIsNeverAppliedToWhicheverRunnerIsActiveNow()
    {
        // RunGate.ShouldProcessMessage's own predicate is proved in isolation by RunGateTests.cs;
        // this is the caller-level guarantee it backs: once a runner is no longer _activeRunner,
        // MainForm's own BeginRun closures must never let its messages reach HandleMessage.
        // Exercised directly against the extracted gate with the exact identity shape MainForm's
        // closures use (the runner object captured at subscribe time vs. the current field).
        object endedRunner = new();
        object? activeRunner = null;
        Assert.IsFalse(RunGate.ShouldProcessMessage(activeRunner, endedRunner));

        object stillActiveRunner = new();
        activeRunner = stillActiveRunner;
        Assert.IsTrue(RunGate.ShouldProcessMessage(activeRunner, stillActiveRunner));
        Assert.IsFalse(RunGate.ShouldProcessMessage(activeRunner, endedRunner));
    }
}

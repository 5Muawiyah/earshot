using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// M4: the window had no control to run the administrator prompt check, though Copy.LockedDetail
// told the owner to run one. Safety comes first here: this test file must NEVER call
// StartRehearsal in a way that could reach ChildRunner.Start against the real
// Test-ElevatedLaunch.ps1, because that raises a real Windows administrator prompt. Every test
// below either drives MainFormTestHarness (which always constructs a sandboxed MainForm, so the
// button's own click handler refuses before it ever builds a ChildRunner) or calls
// RehearsalLaunch's pure path functions directly, which start nothing.
[TestClass]
public sealed class RehearsalControlTests
{
    [TestMethod]
    public void ScriptPathPointsAtTheRealRehearsalScript()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string path = RehearsalLaunch.ScriptPath(repoRoot);
        Assert.IsTrue(File.Exists(path), "RehearsalLaunch.ScriptPath does not point at a real file: " + path);
        StringAssert.EndsWith(path, Path.Combine("gui", "Test-ElevatedLaunch.ps1"));
    }

    [TestMethod]
    public void DriverPathPointsAtTheProductionDriverNeverTheSandboxOne()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        string path = RehearsalLaunch.DriverPath(repoRoot);
        Assert.IsTrue(File.Exists(path));
        StringAssert.EndsWith(path, Path.Combine("gui", "Invoke-GuiHalf.ps1"));
        Assert.IsFalse(path.Contains("selftest", StringComparison.OrdinalIgnoreCase),
            "the rehearsal must never run through the sandbox driver: D8 says nothing about this site may be faked.");
    }

    // M4's own safety rule: an unattended or development sandbox must never run this, because it
    // always raises a real Windows prompt, in any mode. MainFormTestHarness always sandboxes.
    [TestMethod]
    public void TheRehearsalButtonIsDisabledInASandboxWindow()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsFalse(form.RehearsalButtonEnabledForTests);
        });
    }

    // Belt and braces (design.md section 8.1's own phrase for the sandbox refusal): even a click
    // that reaches the handler despite Enabled=false must refuse, in the handler itself.
    [TestMethod]
    public void ClickingTheRehearsalButtonInASandboxWindowStartsNothing()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickRehearsalButtonForTests();

            Assert.IsNull(form.ActiveRunnerForTests, "a sandboxed window must never start the rehearsal, whatever called the click handler.");
            StringAssert.Contains(form.RehearsalStatusTextForTests, "sandbox");
        });
    }

    [TestMethod]
    public void TheWarningNamesARealPromptAndThatThisHasNeverRunBefore()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            string warning = form.RehearsalWarningTextForTests;
            StringAssert.Contains(warning, "administrator");
            StringAssert.Contains(warning, "Windows will raise");
            StringAssert.Contains(warning, "first time this launch has ever been run");
        });
    }

    [TestMethod]
    public void TheLockedDetailAndTheRehearsalRowNameAgree()
    {
        // Copy.LockedDetail tells the owner to "run the administrator prompt check first";
        // Copy.RehearsalRowName is the control's own label. They must name the same thing.
        StringAssert.Contains(Copy.LockedDetail, "administrator prompt check");
        StringAssert.Contains(Copy.RehearsalRowName.ToLowerInvariant(), "administrator prompt check");
    }

    // M13: rows 00 and 07 must say plainly what is not offered, rather than silently never
    // reaching it. Real MainForm, real row selection, the same UpdateRowDetail every other row
    // uses.
    [TestMethod]
    public void Row00NamesTheMissingUninstallOffer()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("00"));
            StringAssert.Contains(form.RowDetailTextForTests, "never offers to uninstall");
        });
    }

    [TestMethod]
    public void Row07NamesTheMissingPlanB()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("07"));
            StringAssert.Contains(form.RowDetailTextForTests, "not wired to a control");
        });
    }

    // M13: "Wire Copy.DeclinedElevatedPrompt into the result panel." A result.json whose only
    // step is elevated, did not run, and carries an error is exactly section 10.3's own shape,
    // constructed directly (ParsedResult/StepRecord), the same way HandOffTests drives
    // ShowHandOffForTests without a real child.
    [TestMethod]
    public void ADeclinedElevatedStepIsDetectedFromResultJsonAlone()
    {
        var result = new ParsedResult
        {
            Test = "15-uninstall-reversal",
            Overall = "fail",
            Criteria = new[] { new CriterionRecord("uninstall", "Uninstall ran.", "fail", "declined") },
            Findings = Array.Empty<FindingRecord>(),
            StepCount = 1,
            Steps = new[] { new StepRecord(Elevated: true, Ran: false, Error: "skipped at the owner request") },
        };

        Assert.IsTrue(result.HasDeclinedElevatedStep);

        ResultPresentation presentation = ResultPresenter.Present(result, @"C:\nowhere");
        Assert.IsTrue(presentation.HasDeclinedElevatedStep);
    }

    [TestMethod]
    public void AStepThatRanIsNeverADeclinedElevatedStep()
    {
        var result = new ParsedResult
        {
            Test = "15-uninstall-reversal",
            Overall = "pass",
            Criteria = new[] { new CriterionRecord("uninstall", "Uninstall ran.", "pass", string.Empty) },
            Findings = Array.Empty<FindingRecord>(),
            StepCount = 1,
            Steps = new[] { new StepRecord(Elevated: true, Ran: true, Error: null) },
        };

        Assert.IsFalse(result.HasDeclinedElevatedStep);
    }
}

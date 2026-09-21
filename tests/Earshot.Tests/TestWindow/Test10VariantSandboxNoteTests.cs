using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// tools\live-tests\selftest\Fakes.psm1 has a StartStates entry for "10-shutdown-messages-v1" only:
// variants 2 to 5 have none, so Initialize-FakeMachine throws immediately for them (see
// Test10VariantSmokeTests's own header comment). That is a gap in the sandbox's own fixture data,
// never something a sandboxed window run can quietly paper over, so the row detail area says so
// rather than leaving the owner to find out by starting one and watching it fail.
[TestClass]
public sealed class Test10VariantSandboxNoteTests
{
    // Each variant gets its own form: ListView.SelectedIndices accumulates across selections
    // (MultiSelect defaults to true and SelectRowForTests never clears an earlier selection), so a
    // single form driven through several variants in one body would read the wrong row's text the
    // moment more than one item stayed selected. Every other *ForTests test in this project also
    // selects exactly one row per form for the same reason.
    [TestMethod]
    public void Variant1ShowsNoSandboxWarning()
    {
        using var sandbox = new Earshot.Tests.TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("10.1"));
            Assert.IsFalse(form.RowDetailTextForTests.Contains(Copy.Test10VariantNotSandboxTestable, StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void Variants2To5ShowTheSandboxFixtureWarning()
    {
        for (int variant = 2; variant <= 5; variant++)
        {
            using var sandbox = new Earshot.Tests.TempFolder();
            int capturedVariant = variant;
            MainFormTestHarness.Run(sandbox.Path, form =>
            {
                Assert.IsTrue(form.SelectRowForTests("10." + capturedVariant));
                StringAssert.Contains(form.RowDetailTextForTests, Copy.Test10VariantNotSandboxTestable,
                    "Variant " + capturedVariant + " did not show the sandbox-fixture warning.");
            });
        }
    }

    // A pure check (Test10VariantSandboxNote.ShouldShow), not a real window: this window's own
    // tests build sandbox forms only, and proving "never shown outside a sandbox window" never
    // needed a real, non-sandboxed MainForm in the first place, since the decision itself never
    // reads anything else about the form.
    [TestMethod]
    public void TheWarningIsNeverShownOutsideASandboxWindow()
    {
        Assert.IsFalse(Test10VariantSandboxNote.ShouldShow(sandboxed: false, rowNumber: "10", variantNumber: 3));
    }

    [TestMethod]
    public void TheWarningNeverShowsForAnyOtherRowEvenSandboxed()
    {
        Assert.IsFalse(Test10VariantSandboxNote.ShouldShow(sandboxed: true, rowNumber: "01", variantNumber: 0));
    }

    [TestMethod]
    public void TheWarningNeverShowsForVariant1Sandboxed()
    {
        Assert.IsFalse(Test10VariantSandboxNote.ShouldShow(sandboxed: true, rowNumber: "10", variantNumber: 1));
    }
}

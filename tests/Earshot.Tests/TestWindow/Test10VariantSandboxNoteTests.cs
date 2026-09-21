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

    [TestMethod]
    public void TheWarningIsNeverShownOutsideASandboxWindow()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        var rows = Earshot.TestWindow.Core.Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        var wording = Earshot.TestWindow.Core.Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainForm? form = null;
            try
            {
                form = new MainForm(repoRoot, rows, wording, sandbox: null, @"C:\nowhere\Earshot.exe");
                form.ForceControlCreationForTests();
                Assert.IsTrue(form.SelectRowForTests("10.3"));
                Assert.IsFalse(form.RowDetailTextForTests.Contains(Copy.Test10VariantNotSandboxTestable, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                form?.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null)
        {
            throw new InvalidOperationException("Test body failed: " + failure, failure);
        }
    }
}

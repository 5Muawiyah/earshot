using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A full test run used to Show() the real MainForm on the owner's desktop about 167 times (parked at
// -32000,-32000, but Show() activates it, so it took focus from whatever the owner was doing). Every
// test that shows a MainForm now does it on a private desktop, and the seam that shows it refuses the
// owner's desktop outright, so a future test cannot quietly bring it back.
[TestClass]
public sealed class NoFormOnTheOwnersDesktopTests
{
    [TestMethod]
    public void TheHarnessRunsItsBodyOnAPrivateDesktop()
    {
        using var sandbox = new TempFolder();
        string desktop = "";
        MainFormTestHarness.Run(sandbox.Path, _ => desktop = TestDesktop.CurrentName());

        Assert.AreNotEqual(TestDesktop.OwnersDesktopName, desktop, "MainFormTestHarness showed the form on the owner's desktop.");
    }

    [TestMethod]
    public void ShowingTheFormForTestsRefusesTheOwnersDesktop()
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        IReadOnlyList<WordingEntry> wording = Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));
        using var sandbox = new TempFolder();

        string desktop = "";
        Exception? refused = null;
        bool shown = false;
        var thread = new Thread(() =>
        {
            desktop = TestDesktop.CurrentName();
            using var form = new MainForm(repoRoot, rows, wording, new SandboxOptions { Folder = sandbox.Path }, @"C:\nowhere\Earshot.exe");
            try
            {
                form.ForceControlCreationForTests(TestDesktop.IsOwnersDesktop);
            }
            catch (InvalidOperationException ex)
            {
                refused = ex;
            }

            shown = form.Visible;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "The thread did not finish in time.");

        Assert.AreEqual(TestDesktop.OwnersDesktopName, desktop, "Sanity: this thread must be on the owner's desktop for the refusal to mean anything.");
        Assert.IsNotNull(refused, "ForceControlCreationForTests must refuse to show the form on the owner's desktop.");
        Assert.IsFalse(shown, "The form must not have been shown before refusing.");
    }
}

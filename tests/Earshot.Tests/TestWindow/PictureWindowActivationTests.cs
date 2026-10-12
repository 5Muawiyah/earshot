using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The window the pictures are drawn in is shown off screen so no one sees it. Form.Show activates a form unless
// ShowWithoutActivation says otherwise, and on the owner's desktop that would take focus from what they are doing, so the
// picture window must never be activated, however it is shown.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.form.showwithoutactivation
[TestClass]
public sealed class PictureWindowActivationTests
{
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();

    private static MainForm NewForm(string sandbox)
    {
        string repoRoot = RepositoryLocator.RepositoryRoot();
        IReadOnlyList<ManifestRow> rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
        IReadOnlyList<WordingEntry> wording = Wording.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "wording.json"));
        return new MainForm(repoRoot, rows, wording, new SandboxOptions { Folder = sandbox }, @"C:\nowhere\Earshot.exe");
    }

    [TestMethod]
    public void ThePictureWindowIsShownWithoutBeingActivated()
    {
        using var folder = new TempFolder();
        bool? activeForm = null;
        bool? foreground = null;
        bool? withoutActivation = null;

        // On a private desktop of the suite's own, as every test that shows a form does.
        Phase5.CardDesktop.Run(() =>
        {
            using MainForm form = NewForm(folder.Path);
            form.ShowOffScreenForPictures(TestDesktop.IsOwnersDesktop);
            Application.DoEvents();
            activeForm = ReferenceEquals(Form.ActiveForm, form);
            foreground = GetForegroundWindow() == form.Handle;
            withoutActivation = form.ShowsWithoutActivationForTests;
        }, TimeSpan.FromMinutes(2));

        Assert.IsFalse(activeForm, "The picture window became the active form.");
        Assert.IsFalse(foreground, "The picture window became the foreground window of its desktop.");
        Assert.IsTrue(withoutActivation, "The picture window does not ask to be shown without activation.");
    }

    [TestMethod]
    public void TheTestersOwnWindowIsStillActivatedWhenItIsShown()
    {
        using var folder = new TempFolder();
        bool? withoutActivation = null;

        Phase5.CardDesktop.Run(() =>
        {
            using MainForm form = NewForm(folder.Path);
            withoutActivation = form.ShowsWithoutActivationForTests;
        }, TimeSpan.FromMinutes(2));

        Assert.IsFalse(withoutActivation, "Only the picture window is shown without activation.");
    }
}

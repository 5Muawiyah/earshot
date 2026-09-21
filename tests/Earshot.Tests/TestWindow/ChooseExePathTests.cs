using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Earshot.exe used to be fixed to %ProgramFiles%\Earshot\Earshot.exe with no way to change it.
// Real MainForm, a real button click, ChooseExePathDialogForTests standing in for the real
// OpenFileDialog only (nothing here can safely dismiss a real Windows file picker on its own).
[TestClass]
public sealed class ChooseExePathTests
{
    [TestMethod]
    public void AValidChoiceReplacesTheExePathAndIsRememberedAcrossOpens()
    {
        using var sandbox = new TempFolder();
        string exePath = Path.Combine(sandbox.Path, "release", "Earshot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        File.WriteAllText(exePath, string.Empty);

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.AreNotEqual(exePath, form.ExePathForTests);

            form.ChooseExePathDialogForTests = () => exePath;
            form.ClickChooseExeButtonForTests();

            Assert.AreEqual(exePath, form.ExePathForTests);
            StringAssert.Contains(form.ExePathLabelTextForTests, exePath);
        });

        // A fresh MainForm, same sandbox folder: the remembered choice is read back on
        // construction, not only kept in the memory of the form that made it.
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.AreEqual(exePath, form.ExePathForTests, "the chosen exe was not remembered across opens.");
        });
    }

    [TestMethod]
    public void CancellingTheDialogLeavesTheExePathUnchanged()
    {
        using var sandbox = new TempFolder();

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            string before = form.ExePathForTests;
            form.ChooseExePathDialogForTests = () => null;

            form.ClickChooseExeButtonForTests();

            Assert.AreEqual(before, form.ExePathForTests);
        });
    }

    [TestMethod]
    public void AUncPathIsRefusedAndNeverRemembered()
    {
        using var sandbox = new TempFolder();

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            // The plain-mode status line never echoes the raw rejected path
            // (NoBannedWordsInPlainModeTests); technical details is switched on here so this test
            // keeps proving the specific reason ExePathChoice gave, not only a generic message.
            form.ClickTechnicalDetailsCheckBoxForTests();
            string before = form.ExePathForTests;
            form.ChooseExePathDialogForTests = () => @"\\server\share\Earshot.exe";

            form.ClickChooseExeButtonForTests();

            Assert.AreEqual(before, form.ExePathForTests, "a UNC path must never replace the exe path.");
            StringAssert.Contains(form.StatusTextForTests, "network path");
        });
    }

    [TestMethod]
    public void AFileThatDoesNotExistIsRefused()
    {
        using var sandbox = new TempFolder();
        string missing = Path.Combine(sandbox.Path, "nowhere", "Earshot.exe");

        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            form.ClickTechnicalDetailsCheckBoxForTests();
            string before = form.ExePathForTests;
            form.ChooseExePathDialogForTests = () => missing;

            form.ClickChooseExeButtonForTests();

            Assert.AreEqual(before, form.ExePathForTests);
            StringAssert.Contains(form.StatusTextForTests, "does not exist");
        });
    }
}

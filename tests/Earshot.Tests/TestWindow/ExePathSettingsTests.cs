using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// Where a chosen Earshot.exe is remembered between opens: a plain file beside run-all.json
// (MainForm's own WindowStateRoot), read back through the same ExePathChoice validation a fresh
// choice goes through, so a folder moved, an exe deleted, or a hand-edited file can never hand
// back a path that is no longer good.
[TestClass]
public sealed class ExePathSettingsTests
{
    [TestMethod]
    public void NothingRememberedYetReadsNull()
    {
        using var folder = new TempFolder();
        Assert.IsNull(ExePathSettings.TryRead(folder.Path));
    }

    [TestMethod]
    public void AWrittenChoiceReadsBackExactly()
    {
        using var folder = new TempFolder();
        string exePath = folder.File("Earshot.exe");
        File.WriteAllText(exePath, string.Empty);

        ExePathSettings.Write(folder.Path, exePath);

        Assert.AreEqual(exePath, ExePathSettings.TryRead(folder.Path));
    }

    [TestMethod]
    public void ARememberedChoiceThatIsNoLongerValidReadsNullRatherThanTheStalePath()
    {
        using var folder = new TempFolder();
        string exePath = folder.File("Earshot.exe");
        File.WriteAllText(exePath, string.Empty);
        ExePathSettings.Write(folder.Path, exePath);

        File.Delete(exePath);

        Assert.IsNull(ExePathSettings.TryRead(folder.Path), "a remembered path to a file that no longer exists must never be handed back.");
    }
}

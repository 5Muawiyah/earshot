using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The file dialog's own choice is never trusted just because Windows let the owner pick it: it
// has to be a local drive-letter path (never UNC, the same rule resume.txt's own -ExePath
// follows), a file that actually exists, and named exactly Earshot.exe.
[TestClass]
public sealed class ExePathChoiceTests
{
    [TestMethod]
    public void ARealLocalFileNamedEarshotExeIsValid()
    {
        using var folder = new TempFolder();
        string path = folder.File("Earshot.exe");
        File.WriteAllText(path, string.Empty);

        Assert.IsTrue(ExePathChoice.IsValid(path, out string? reason), reason);
    }

    [TestMethod]
    public void AUncPathIsRejected()
    {
        Assert.IsFalse(ExePathChoice.IsValid(@"\\server\share\Earshot.exe", out string? reason));
        StringAssert.Contains(reason, "network path");
    }

    [TestMethod]
    public void ARelativeOrNonDriveLetterPathIsRejected()
    {
        Assert.IsFalse(ExePathChoice.IsValid("Earshot.exe", out string? reason));
        StringAssert.Contains(reason, "local drive letter");
    }

    [TestMethod]
    public void AFileThatIsNotNamedEarshotExeIsRejected()
    {
        using var folder = new TempFolder();
        string path = folder.File("notepad.exe");
        File.WriteAllText(path, string.Empty);

        Assert.IsFalse(ExePathChoice.IsValid(path, out string? reason));
        StringAssert.Contains(reason, "Earshot.exe");
    }

    [TestMethod]
    public void APathThatDoesNotExistIsRejected()
    {
        using var folder = new TempFolder();
        string path = folder.File("Earshot.exe");

        Assert.IsFalse(ExePathChoice.IsValid(path, out string? reason));
        StringAssert.Contains(reason, "does not exist");
    }

    [TestMethod]
    public void AnEmptyOrBlankPathIsRejected()
    {
        Assert.IsFalse(ExePathChoice.IsValid(string.Empty, out string? reason));
        Assert.IsNotNull(reason);
        Assert.IsFalse(ExePathChoice.IsValid("   ", out reason));
        Assert.IsNotNull(reason);
    }
}

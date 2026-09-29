using System.Diagnostics;
using Earshot.Boot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The one execution of the real reader that the controller tests replace with a fake: a file that carries a
// version, one that does not, and one that is not there, read from disk.
[TestClass]
public sealed class InstalledFileReaderTests
{
    [TestMethod]
    public void AFileWithAVersionReadsItsFileVersion()
    {
        string path = typeof(BlockController).Assembly.Location;
        FileVersionInfo expected = FileVersionInfo.GetVersionInfo(path);

        InstalledFile file = new InstalledFileReader().Read(path);

        Assert.IsTrue(file.Present);
        Assert.IsTrue(file.Step.Ok, file.Step.Detail);
        Assert.AreEqual(new Version(expected.FileMajorPart, expected.FileMinorPart, expected.FileBuildPart, expected.FilePrivatePart), file.Version);
    }

    [TestMethod]
    public void AFileThatIsNotThereIsMissingWithItsCode()
    {
        string path = Path.Combine(Path.GetTempPath(), "earshot-no-such-" + Guid.NewGuid().ToString("N"), "Earshot.exe");

        InstalledFile file = new InstalledFileReader().Read(path);

        Assert.IsFalse(file.Present);
        Assert.IsNull(file.Version);
        Assert.IsFalse(file.Step.Ok);
        Assert.AreEqual(unchecked((int)0x80070002), file.Step.Code);
    }

    [TestMethod]
    public void AFileWithNoVersionIsThereButHasNoVersion()
    {
        string path = Path.Combine(Path.GetTempPath(), "earshot-noversion-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "not an executable");
        try
        {
            InstalledFile file = new InstalledFileReader().Read(path);

            Assert.IsTrue(file.Present);
            Assert.IsNull(file.Version, "No version resource is no version, never 0.0.0.0.");
            Assert.IsFalse(file.Step.Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

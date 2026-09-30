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

    // A standard user can hold an installed program open without sharing it. That is a file that could not be read, with the
    // raw sharing code, not one that has no version: the log says which, and nothing is decided from either.
    [TestMethod]
    public void AFileHeldOpenWithoutSharingCouldNotBeReadAndSaysSoWithTheSharingCode()
    {
        string path = Path.Combine(Path.GetTempPath(), "earshot-held-" + Guid.NewGuid().ToString("N") + ".dll");
        File.Copy(typeof(BlockController).Assembly.Location, path);
        try
        {
            InstalledFile file;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                file = new InstalledFileReader().Read(path);
            }

            Assert.IsTrue(file.Present, "The file is there.");
            Assert.IsNull(file.Version);
            Assert.IsFalse(file.Step.Ok);
            Assert.AreEqual(unchecked((int)0x80070020), file.Step.Code, "The sharing violation, raw: " + file.Step.Detail);
            StringAssert.Contains(file.Step.Detail, "could not be read");
            Assert.DoesNotContain("no file version", file.Step.Detail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void AFileWithNoVersionSaysItCarriesNone()
    {
        string path = Path.Combine(Path.GetTempPath(), "earshot-noversion-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "not an executable");
        try
        {
            InstalledFile file = new InstalledFileReader().Read(path);

            Assert.IsTrue(file.Present);
            Assert.IsNull(file.Version);
            StringAssert.Contains(file.Step.Detail, "carries no file version");
            Assert.AreEqual(Earshot.Contracts.NativeCodes.NotAvailable, file.Step.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

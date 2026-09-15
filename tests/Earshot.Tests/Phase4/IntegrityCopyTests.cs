using System.Security.Cryptography;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class IntegrityCopyTests
{
    private static string MakeSource(TempFolder temp)
    {
        string source = temp.File("source");
        Directory.CreateDirectory(Path.Combine(source, "a", "b"));
        File.WriteAllBytes(Path.Combine(source, "Earshot.exe"), RandomNumberGenerator.GetBytes(200_000));
        File.WriteAllBytes(Path.Combine(source, "a", "one.dll"), RandomNumberGenerator.GetBytes(1));
        File.WriteAllBytes(Path.Combine(source, "a", "b", "empty.json"), []);
        return source;
    }

    [TestMethod]
    public void CopiesEveryFileAndRecordsItsHash()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string destination = temp.File("destination");

        IntegrityCopyResult result = IntegrityCopy.Copy(source, destination);

        Assert.IsTrue(result.Ok, string.Join(" | ", result.Steps.Select(s => s.Detail)));
        Assert.HasCount(3, result.Files);
        foreach (CopiedFile file in result.Files)
        {
            byte[] original = File.ReadAllBytes(Path.Combine(source, file.RelativePath));
            byte[] copy = File.ReadAllBytes(Path.Combine(destination, file.RelativePath));
            CollectionAssert.AreEqual(original, copy, file.RelativePath);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(original)), file.Sha256);
            Assert.AreEqual(original.LongLength, file.Length);
        }
    }

    [TestMethod]
    public void AFileSomeoneIsWritingCannotBeCopied()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string destination = temp.File("destination");

        // Another writer holds the file: opening it with FileShare.Read (deny write) conflicts with that
        // writer, which is the same rule that keeps a writer out once the copy holds the file.
        using (new FileStream(Path.Combine(source, "a", "one.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            IntegrityCopyResult result = IntegrityCopy.Copy(source, destination);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("0x80070020", result.Steps.Single().CodeName, "ERROR_SHARING_VIOLATION as an HRESULT.");
        }
    }

    [TestMethod]
    public void WhileTheCopyHoldsAFileNoOneCanWriteRenameOrDeleteIt()
    {
        using var temp = new TempFolder();
        string path = temp.File("held.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        // The share mode IntegrityCopy opens each source file with.
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.ThrowsExactly<IOException>(() => File.WriteAllBytes(path, [9]));
        Assert.ThrowsExactly<IOException>(() => File.Move(path, temp.File("moved.bin")));
        Assert.ThrowsExactly<IOException>(() => File.Delete(path));
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.AreEqual(3, reader.Length, "Other readers are still allowed.");
    }

    [TestMethod]
    public void AnExistingDestinationFileFailsTheCopy()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string destination = temp.File("destination");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "Earshot.exe"), "planted");

        IntegrityCopyResult result = IntegrityCopy.Copy(source, destination);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("planted", File.ReadAllText(Path.Combine(destination, "Earshot.exe")));
    }

    [TestMethod]
    public void OverlappingFoldersAreRefused()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);

        Assert.IsFalse(IntegrityCopy.Copy(source, Path.Combine(source, "inner")).Ok);
        Assert.IsFalse(IntegrityCopy.Copy(source, source).Ok);
        Assert.IsFalse(Directory.Exists(Path.Combine(source, "inner")));
    }

    [TestMethod]
    public void AMissingSourceFails()
    {
        using var temp = new TempFolder();

        IntegrityCopyResult result = IntegrityCopy.Copy(temp.File("absent"), temp.File("destination"));

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("ERROR_FILE_NOT_FOUND", result.Steps.Single().CodeName, "CreateFileW on the source folder.");
    }

    [TestMethod]
    public void AJunctionInsideTheSourceFailsTheCopy()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string outside = temp.File("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        using IDisposable junction = TestLinks.CreateJunction(Path.Combine(source, "a", "link"), outside);
        string destination = temp.File("destination");

        IntegrityCopyResult result = IntegrityCopy.Copy(source, destination);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Steps.Single().Detail, "reparse point");
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public void ASourceFolderThatIsAJunctionFailsTheCopy()
    {
        using var temp = new TempFolder();
        string real = MakeSource(temp);
        string source = temp.File("unzip");
        using IDisposable junction = TestLinks.CreateJunction(source, real);

        IntegrityCopyResult result = IntegrityCopy.Copy(source, temp.File("destination"));

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Steps.Single().Detail, "reparse point");
    }

    // The window the listing check alone left open: a subfolder is swapped for a junction after the source
    // was listed and before its files are opened. The open follows the junction; the handle's final path
    // shows it and the copy stops before anything is read or written.
    [TestMethod]
    public void ASubfolderSwappedForAJunctionAfterListingFailsTheCopy()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string outside = temp.File("outside");
        Directory.CreateDirectory(Path.Combine(outside, "b"));
        File.WriteAllText(Path.Combine(outside, "one.dll"), "outside");
        File.WriteAllText(Path.Combine(outside, "b", "empty.json"), "outside");
        string destination = temp.File("destination");
        IDisposable? junction = null;
        try
        {
            IntegrityCopyResult result = IntegrityCopy.Copy(source, destination, afterListing: () =>
            {
                Directory.Move(Path.Combine(source, "a"), Path.Combine(source, "a-old"));
                junction = TestLinks.CreateJunction(Path.Combine(source, "a"), outside);
            });

            Assert.IsNotNull(junction, "The swap ran.");
            Assert.IsFalse(result.Ok);
            StepOutcome step = result.Steps.Single();
            StringAssert.StartsWith(step.Step, "copy-app:a");
            StringAssert.Contains(step.Detail, "not the one listed");
            StringAssert.Contains(step.Detail, "outside");
            Assert.IsEmpty(result.Files);
            Assert.IsFalse(Directory.Exists(destination), "Nothing is written.");
        }
        finally
        {
            junction?.Dispose();
        }
    }

    [TestMethod]
    public void ASourceFileWithASecondHardLinkFailsTheCopy()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string outside = temp.File("outside.bin");
        File.WriteAllText(outside, "outside");
        TestLinks.CreateHardLink(Path.Combine(source, "a", "linked.dll"), outside);

        IntegrityCopyResult result = IntegrityCopy.Copy(source, temp.File("destination"));

        Assert.IsFalse(result.Ok);
        StepOutcome step = result.Steps.Single();
        Assert.AreEqual(@"copy-app:a\linked.dll", step.Step);
        StringAssert.Contains(step.Detail, "2 hard links");
    }

    [TestMethod]
    public void WhileTheCopyRunsTheSourceFolderCannotBeRenamed()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        Exception? renameError = null;

        IntegrityCopyResult result = IntegrityCopy.Copy(source, temp.File("destination"), afterListing: () =>
        {
            try
            {
                Directory.Move(source, temp.File("renamed"));
            }
            catch (IOException ex)
            {
                renameError = ex;
            }
        });

        Assert.IsNotNull(renameError, "The held folder handle denies the rename.");
        Assert.AreEqual(unchecked((int)0x80070020), renameError.HResult, "ERROR_SHARING_VIOLATION as an HRESULT.");
        Assert.IsTrue(result.Ok, string.Join(" | ", result.Steps.Select(s => s.Detail)));
    }

    [TestMethod]
    public void ALinkInsideTheSourceFailsTheCopy()
    {
        using var temp = new TempFolder();
        string source = MakeSource(temp);
        string outside = temp.File("outside");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(source, "a", "link"), outside);
        }
        catch (IOException ex)
        {
            Assert.Inconclusive("This account cannot create a symbolic link: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Inconclusive("This account cannot create a symbolic link: " + ex.Message);
        }

        IntegrityCopyResult result = IntegrityCopy.Copy(source, temp.File("destination"));

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Steps.Single().Detail, "reparse point");
    }

    [TestMethod]
    public void IsInsideComparesWholePathSegments()
    {
        Assert.IsTrue(IntegrityCopy.IsInside(@"C:\Program Files\Earshot\x.dll", @"C:\Program Files\Earshot"));
        Assert.IsTrue(IntegrityCopy.IsInside(@"C:\Program Files\Earshot\", @"c:\program files\earshot"));
        Assert.IsFalse(IntegrityCopy.IsInside(@"C:\Program Files\Earshot2\x.dll", @"C:\Program Files\Earshot"));
        Assert.IsFalse(IntegrityCopy.IsInside(@"C:\Program Files", @"C:\Program Files\Earshot"));
    }
}

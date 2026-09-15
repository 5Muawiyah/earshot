using System.Security.Cryptography;
using Earshot.Boot.Gate;
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
        Assert.AreEqual("ERROR_PATH_NOT_FOUND", result.Steps.Single().CodeName);
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

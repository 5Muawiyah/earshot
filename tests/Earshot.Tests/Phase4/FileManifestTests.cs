using System.Text;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The publish manifest as the build writes it (UTF-8 with a byte order mark, CRLF, "/" separators) and as
// install reads it. Reading changes nothing; no file is installed here.
[TestClass]
public sealed class FileManifestTests
{
    private const string Hash = "87D7E0A7A9CD70C60243B36D5E37967BCBA978775DC3F5665459CDCE728939C4";

    // Exactly the shape the WriteEarshotFileManifest target produced for a framework-dependent publish.
    private const string Published =
        "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" +
        "    { \"Path\": \"Earshot.deps.json\", \"Sha256\": \"BBD0FD2FB920CCF97B8C41C393AECE94394A98C35D353C35412959A661B47331\" },\r\n" +
        "    { \"Path\": \"Earshot.dll\", \"Sha256\": \"" + Hash + "\" },\r\n" +
        "    { \"Path\": \"runtimes/win-x64/native/one.dll\", \"Sha256\": \"" + Hash + "\" }\r\n" +
        "  ]\r\n}\r\n";

    private static FileManifest? Read(TempFolder temp, string content, out StepOutcome step)
    {
        File.WriteAllText(Path.Combine(temp.Path, FileManifest.FileName), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return FileManifest.Read(temp.Path, out step);
    }

    [TestMethod]
    public void ThePublishedManifestReadsBackWithEveryFile()
    {
        using var temp = new TempFolder();

        FileManifest? manifest = Read(temp, Published, out StepOutcome step);

        Assert.IsNotNull(manifest, step.Detail);
        Assert.IsTrue(step.Ok);
        Assert.HasCount(3, manifest.Files);
        Assert.AreEqual("Earshot.dll", manifest.Files[1].RelativePath);
        Assert.AreEqual(Path.Combine("runtimes", "win-x64", "native", "one.dll"), manifest.Files[2].RelativePath, "Separators are the ones this machine uses.");
        Assert.AreEqual(Hash, manifest.HashOf("Earshot.dll"));
        Assert.AreEqual(Hash, manifest.HashOf(Path.Combine("runtimes", "win-x64", "native", "one.dll")));
        Assert.IsNull(manifest.HashOf("somethingelse.dll"));
    }

    [TestMethod]
    public void AMissingManifestSaysToInstallFromAReleaseBuild()
    {
        using var temp = new TempFolder();

        FileManifest? manifest = FileManifest.Read(temp.Path, out StepOutcome step);

        Assert.IsNull(manifest);
        Assert.IsFalse(step.Ok);
        Assert.AreEqual("ERROR_FILE_NOT_FOUND", step.CodeName);
        StringAssert.Contains(step.Detail, FileManifest.MissingMessage);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("[]")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [], \"Extra\": 1 }")]
    [DataRow("{ \"SchemaVersion\": 0, \"Files\": [ { \"Path\": \"a.dll\", \"Sha256\": \"" + Hash + "\" } ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"a.dll\", \"Sha256\": \"" + Hash + "\" }, ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"a.dll\", \"Sha256\": \"" + Hash + "\", \"Size\": 1 } ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"a.dll\", \"Sha256\": \"" + Hash + "\" }, { \"Path\": \"A.DLL\", \"Sha256\": \"" + Hash + "\" } ] }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": 1, \"Sha256\": \"" + Hash + "\" } ] }")]
    public void AnythingButThePublishedShapeIsRefused(string content)
    {
        using var temp = new TempFolder();

        FileManifest? manifest = Read(temp, content, out StepOutcome step);

        Assert.IsNull(manifest);
        Assert.IsFalse(step.Ok);
        StringAssert.Contains(step.Detail, FileManifest.MissingMessage);
    }

    [TestMethod]
    [DataRow(@"..\Earshot.exe")]
    [DataRow("../Earshot.exe")]
    [DataRow("a/../../Earshot.exe")]
    [DataRow("/Earshot.exe")]
    [DataRow("C:/Earshot.exe")]
    [DataRow(@"C:\Earshot.exe")]
    [DataRow(@"\\server\share\Earshot.exe")]
    [DataRow("a//b.dll")]
    [DataRow("")]
    [DataRow(".")]
    [DataRow("a/")]
    [DataRow("a/b .dll ")]
    [DataRow("a/b.dll.")]
    [DataRow("a/b:stream.dll")]
    [DataRow("a/b*.dll")]
    [DataRow("a\u0000b.dll")]
    public void APathThatCouldLeaveTheFolderIsNotARelativePath(string path)
    {
        Assert.IsFalse(FileManifest.IsRelativePath(path), path);
    }

    [TestMethod]
    [DataRow("Earshot.exe")]
    [DataRow("runtimes/win-x64/native/one.dll")]
    [DataRow("a b/c-d_e.f.dll")]
    public void APlainRelativePathIsAccepted(string path)
    {
        Assert.IsTrue(FileManifest.IsRelativePath(path), path);
    }

    [TestMethod]
    public void AHashIsSixtyFourHexCharacters()
    {
        Assert.IsTrue(FileManifest.IsSha256(Hash));
        Assert.IsTrue(FileManifest.IsSha256(Hash.ToLowerInvariant()));
        Assert.IsFalse(FileManifest.IsSha256(Hash[..63]));
        Assert.IsFalse(FileManifest.IsSha256(Hash + "0"));
        Assert.IsFalse(FileManifest.IsSha256(new string('g', 64)));
    }

    [TestMethod]
    public void AManifestLargerThanTheCapIsRefused()
    {
        using var temp = new TempFolder();
        string padding = new('a', FileManifest.MaxBytes);

        FileManifest? manifest = Read(temp, "{ \"SchemaVersion\": 1, \"Files\": [ { \"Path\": \"" + padding + "\", \"Sha256\": \"" + Hash + "\" } ] }", out StepOutcome step);

        Assert.IsNull(manifest);
        StringAssert.Contains(step.Detail, "larger than");
    }
}

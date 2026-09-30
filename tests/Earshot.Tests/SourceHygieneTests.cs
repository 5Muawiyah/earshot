using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests;

// Properties of the source tree itself, checked by reading it. A raw NUL byte in a text file makes git treat the whole file as
// binary: it shows no diff and cannot be reviewed line by line, so a C# char literal of NUL is written '\0', never typed.
[TestClass]
public sealed class SourceHygieneTests
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".slnx", ".sln", ".ps1", ".psm1", ".psd1", ".cmd", ".bat", ".md", ".json", ".yml", ".yaml",
        ".txt", ".xml", ".resx", ".html", ".css", ".js", ".editorconfig", ".gitattributes", ".gitignore",
    };

    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", "node_modules", "artifacts", "TestResults"];

    // Every text file under the folder that holds a NUL byte, as "relative path (offset of the first)". Folders of build output
    // are not source and are skipped.
    internal static List<string> FilesWithNul(string root)
    {
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Take(parts.Length - 1).Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (!TextExtensions.Contains(Path.GetExtension(file)) && !TextExtensions.Contains(name))
            {
                continue;
            }

            int offset = Array.IndexOf(File.ReadAllBytes(file), (byte)0);
            if (offset >= 0)
            {
                found.Add(relative + " (byte " + offset + ")");
            }
        }

        return found;
    }

    [TestMethod]
    public void NoSourceFileHoldsARawNulByte()
    {
        string repoRoot = FindRepoRoot(ThisFilePath());

        List<string> found = FilesWithNul(repoRoot);

        Assert.IsEmpty(found, "Raw NUL bytes make git treat a file as binary. Write a NUL char literal as '\\0'. Found in: " + string.Join(", ", found));
    }

    // The scan itself, run for real: it finds a NUL where there is one and is quiet where there is none.
    [TestMethod]
    public void TheScanFindsANulByteInATextFileAndNotInACleanOne()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        File.WriteAllBytes(Path.Combine(temp.Path, "src", "Bad.cs"), [.. "var c = '"u8.ToArray(), 0, .. "';"u8.ToArray()]);
        File.WriteAllText(Path.Combine(temp.Path, "src", "Good.cs"), "var c = '\\0';");
        Directory.CreateDirectory(Path.Combine(temp.Path, "src", "bin"));
        File.WriteAllBytes(Path.Combine(temp.Path, "src", "bin", "Output.json"), [0, 1, 2]);
        File.WriteAllBytes(Path.Combine(temp.Path, "src", "Picture.png"), [0, 1, 2]);

        List<string> found = FilesWithNul(temp.Path);

        Assert.AreEqual(1, found.Count, string.Join(", ", found));
        StringAssert.StartsWith(found[0], Path.Combine("src", "Bad.cs"));
    }

    private static string FindRepoRoot(string thisFile)
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "Could not find the repository root (Earshot.slnx) above " + thisFile);
        return dir.FullName;
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

using System.Runtime.CompilerServices;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The same search as tests\Earshot.Tests\TestWindow\NoDocumentCitationsTests, over the harness's
// own owned paths, which that class's OwnedFolders does not reach: tools\live-tests (the shipped
// scripts, LiveTest.psm1 and tools\live-tests\selftest together) and this folder,
// tests\Earshot.Tests\LiveTests. A comment naming a section number, a short internal review-round
// label, or a design document's own slug reads fine while that document sits open beside the code
// and like a locked door the moment it does not: nobody reading only this repository can look it
// up. This reuses NoDocumentCitationsTests.Citation itself, not a second copy of the pattern text,
// so the two searches can never silently drift apart.
[TestClass]
public sealed class NoDocumentCitationsInLiveTestsTests
{
    private static readonly string[] OwnedFolders =
    {
        Path.Combine("tools", "live-tests"),
        Path.Combine("tests", "Earshot.Tests", "LiveTests"),
    };

    // This file's own path, captured at compile time, the same reason NoDocumentCitationsTests
    // excludes itself: this file's own source is not a comment citing anything, even where it
    // names, in the abstract, the kind of text the search looks for.
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    [TestMethod]
    public void NoCommentInTheLiveTestHarnessCitesADocumentThatIsNotInTheRepository()
    {
        string root = RepositoryRoot();
        string thisFile = Path.GetFullPath(ThisFilePath());
        var found = new List<string>();
        int filesRead = 0;

        foreach (string folder in OwnedFolders)
        {
            string full = Path.Combine(root, folder);
            Assert.IsTrue(Directory.Exists(full), "Owned folder not found: " + full);

            foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                filesRead++;

                string fileName = Path.GetFileName(file);
                if (NoDocumentCitationsTests.Citation.IsMatch(fileName))
                {
                    found.Add(Path.GetRelativePath(root, file) + ": file name itself: " + fileName);
                }

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (NoDocumentCitationsTests.Citation.IsMatch(lines[i]))
                    {
                        found.Add(Path.GetRelativePath(root, file) + ":" + (i + 1) + ": " + lines[i].Trim());
                    }
                }
            }
        }

        Assert.IsTrue(filesRead >= 20, "Only " + filesRead + " files were read across " + string.Join(", ", OwnedFolders) + ", so a clean result would prove nothing.");
        Assert.AreEqual(0, found.Count, "A comment still cites a document that is not in this repository:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}

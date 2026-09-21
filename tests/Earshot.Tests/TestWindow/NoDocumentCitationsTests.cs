using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A comment that cites a section number or a short internal review-round label from a design
// document that lives outside this repository reads fine while that document sits open beside the
// code, and reads like a locked door the moment it does not: nobody reading only this repository
// can look that external reference up. Every comment under this window's own owned paths has to
// say what the code does and why in its own terms instead. This is the static check that keeps one
// from coming back: it runs the same search a human reviewer used to find and fix the first 69,
// over the same three folders, and fails naming the file and line of anything it still finds.
[TestClass]
public sealed class NoDocumentCitationsTests
{
    // The exact search used to find the citations this class exists to keep out: the word
    // "section" immediately followed by a number (optionally with a decimal part), a short
    // internal label made of a single letter immediately followed by one or two digits and then a
    // colon, a phrase naming a round of review, or that same short label carried into an
    // identifier or a file name itself (B1SingleRunnerTests, M4Something): the label followed by
    // an upper-case letter that itself starts a real word (a lower-case letter after it,
    // PascalCase's own shape), never merely by a colon. The lower-case-letter requirement is what
    // keeps this from also flagging an upper-case hex address a test fixture writes
    // ("B4C5D6E7F809": "C" is followed by the digit "5", never a lower-case letter, so no word
    // ever starts there). Kept in the same shape as the search that found the first 69, so the two
    // can never silently drift apart.
    private static readonly Regex Citation = new(
        @"\bsection [0-9]+(\.[0-9]+)?\b|\b[BMmHST][0-9]{1,2}\b:|\b[BMmHST][0-9]{1,2}(?=[A-Z][a-z])|review round",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] OwnedFolders =
    {
        Path.Combine("src", "Earshot.TestWindow"),
        Path.Combine("tests", "Earshot.Tests", "TestWindow"),
        Path.Combine("tools", "live-tests", "gui"),
    };

    // This file's own path, captured at compile time. Citation's pattern has to appear literally
    // in the regex definition above for this check to work at all, so this one file is the single
    // exception to "no comment may match it": it is excluded from the scan below, not because its
    // comments are special, but because the check's own source code is not a comment citing
    // anything.
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    [TestMethod]
    public void NoCommentCitesADocumentThatIsNotInTheRepository()
    {
        string root = RepositoryLocator.RepositoryRoot();
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

                // The file's own name carries a label just as a class or member name inside it
                // can (B1SingleRunnerTests.cs), and nothing above ever reads a file name as a
                // "line", so this is checked once here rather than being silently skipped.
                string fileName = Path.GetFileName(file);
                if (Citation.IsMatch(fileName))
                {
                    found.Add(Path.GetRelativePath(root, file) + ": file name itself: " + fileName);
                }

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (Citation.IsMatch(lines[i]))
                    {
                        found.Add(Path.GetRelativePath(root, file) + ":" + (i + 1) + ": " + lines[i].Trim());
                    }
                }
            }
        }

        Assert.IsTrue(filesRead >= 40, "Only " + filesRead + " files were read across " + string.Join(", ", OwnedFolders) + ", so a clean result would prove nothing.");
        Assert.AreEqual(0, found.Count, "A comment still cites a document that is not in this repository:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase);
    }
}

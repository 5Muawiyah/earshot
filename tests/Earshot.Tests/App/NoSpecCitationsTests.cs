using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The window's own NoDocumentCitationsTests (tests\Earshot.Tests\TestWindow) keeps that window's three
// folders free of a reference to a design document or a review round that lives outside this repository.
// This is the same idea over the other two trees a reviewer actually reads: src\Earshot itself and the
// whole of tests\Earshot.Tests, TestWindow included, so the two checks overlap there on purpose rather
// than leaving a seam between them. A citation reads fine while the document sits open beside the code and
// like a locked door the moment it does not: everything under these two trees has to say what the code
// does and why in its own terms.
[TestClass]
public sealed class NoSpecCitationsTests
{
    // The label shape used across every review round so far, B1, B2, M1 to M6, m1 to m12, S1 to S8 (the
    // live-test samples), T1 to T15, D2 to D8 (design-document decisions) and L6, L17 (design addenda): one
    // of those letters immediately followed by one or two digits, then either a colon, a possessive ("D2's
    // order", "S8's own acceptance line"), or "says" ("D8 says nothing about this site may be faked"). The
    // letter set is the project's own label alphabet, not every letter, so this never flags an architecture
    // reference such as "on x64:" (BluetoothApis.cs), and the trailing anchor is what keeps it from
    // flagging a .NET format string such as ("D2", CultureInfo.InvariantCulture): the characters right
    // after "D2" there are a closing quote and a comma, never one of the three this looks for. "section N"
    // and "N.N" cover a design document's own numbering; "review round" and the two project slugs cover
    // the documents by name.
    private static readonly Regex Citation = new(
        @"\bsection [0-9]+(\.[0-9]+)?\b|\b[BDHLMmST][0-9]{1,2}\b(?::|'s\b| says\b)|\b[BDHLMmST][0-9]{1,2}(?=[A-Z][a-z])|review round|handback-on-shutdown-and-sleep|handback-review",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] OwnedFolders =
    {
        Path.Combine("src", "Earshot"),
        Path.Combine("tests", "Earshot.Tests"),
    };

    // This file's own path, captured at compile time, the same trick NoDocumentCitationsTests uses: the
    // pattern has to appear literally in the regex above, so this one file is excluded from the scan below,
    // not because its comments are special but because the check's own source is not a citation of anything.
    // TestWindow's own NoDocumentCitationsTests.cs is excluded for the identical reason: its own regex and
    // doc comments necessarily spell out example labels (B1SingleRunnerTests, M4Something) to say what they
    // catch, and that file is already read by its own, narrower check.
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    private static readonly string[] SelfExcludedFileNames = { "NoSpecCitationsTests.cs", "NoDocumentCitationsTests.cs" };

    [TestMethod]
    public void NoCommentOrTestNameCitesADesignDocumentOrAReviewRound()
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
                if (IsBuildOutput(file) ||
                    string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase) ||
                    SelfExcludedFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                filesRead++;

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

        Assert.IsTrue(filesRead >= 300, "Only " + filesRead + " files were read across " + string.Join(", ", OwnedFolders) + ", so a clean result would prove nothing.");
        Assert.AreEqual(0, found.Count, "A comment or name still cites a document that is not in this repository:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase);
    }
}

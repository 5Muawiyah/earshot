using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Fixtures, and the code that reads them, are synthetic only. Scans every .cs file under
// src\Earshot\Widget and tests\Earshot.Tests\Widget (including WidgetFixtures.cs itself, unlike the prior
// version of this test) for anything shaped like a real device: a six-group colon- or dash-separated
// Bluetooth address, any shorter colon/dash-separated hex run, a bare 12-hex-digit address with no
// separator, a ulong-shaped hex literal, a plain hex payload of 16 bytes or more (checked across the whole
// file, not line by line, so a run split by a formatter across several lines is still caught), and the words
// "Find My". The only byte runs let through are the fixture's own documented synthetic ones, listed
// explicitly: the 0x10..0x1F and 0x20..0x2F fillers.
[TestClass]
public sealed class WidgetFixtureHygieneTests
{
    // Six colon- or dash-separated hex pairs: the documented Bluetooth address text form.
    private static readonly Regex SixGroupHexAddress = new(
        @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Two or more colon- or dash-separated hex pairs: a partial address is still address-shaped.
    private static readonly Regex SeparatedHexRun = new(
        @"\b([0-9A-Fa-f]{2}[:-]){2,}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 12 hex digits with no separator at all: a MAC-style address written as one run.
    private static readonly Regex TwelveDigitHexAddress = new(
        @"\b[0-9A-Fa-f]{12}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 0x followed by 12 or more hex digits: a 48-bit Bluetooth address packed into a ulong needs exactly 12,
    // so this starts above the width of an ordinary 8-digit HRESULT or CONFIGRET literal (0x80004005 and
    // the like are routine in this codebase and are not addresses). An optional UL/LU-style C# integer
    // literal suffix is consumed too: \b does not sit between two word characters, so "...ABUL" would
    // otherwise never reach a boundary right after the hex digits and the literal would go uncaught.
    private static readonly Regex UlongHexLiteral = new(
        @"\b0x[0-9A-Fa-f]{12,}(?:[uU][lL]?|[lL][uU]?)?\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 16 or more hex byte literals in a row (a captured payload's shape) ignoring the punctuation a byte
    // array initialiser uses, or 32 or more bare hex digits in a row: applied to the whole file so a run a
    // formatter split across lines is still one match, not sixteen separate two-digit non-matches.
    private static readonly Regex LongHexRun = new(
        @"(?:0x[0-9A-Fa-f]{2}[,\s]*){16,}|[0-9A-Fa-f]{32,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    // 16 or more decimal byte values (0-255) in a row, comma- or whitespace-separated: the same captured-
    // payload shape as LongHexRun, written in decimal instead of hex.
    private static readonly Regex LongDecimalByteRun = new(
        @"(?:\b(?:25[0-5]|2[0-4]\d|1\d\d|\d\d?)\b[,\s]+){15,}\b(?:25[0-5]|2[0-4]\d|1\d\d|\d\d?)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    // The only byte runs a fixture may hold: the two 16-byte fillers WidgetFixtures.cs builds
    // (ProximityValue's encrypted-payload run and UnknownSeventeenByteForm's run). Compared against the
    // run's hex digits alone (punctuation stripped), so it catches the sequence whether written as a
    // byte-array initialiser or a bare hex string.
    private static readonly string[] AllowedSyntheticRuns =
    {
        "101112131415161718191A1B1C1D1E1F", // WidgetFixtures.Proximity's encrypted-payload filler
        "202122232425262728292A2B2C2D2E2F", // WidgetFixtures.UnknownSeventeenByteForm's filler
    };

    [TestMethod]
    public void NoFixtureHoldsAnAddressACapturedPayloadOrAName()
    {
        string thisFile = Path.GetFullPath(ThisFilePath());
        string repoRoot = FindRepoRoot(thisFile);
        string[] folders =
        {
            Path.Combine(repoRoot, "src", "Earshot", "Widget"),
            Path.Combine(repoRoot, "tests", "Earshot.Tests", "Widget"),
        };

        var found = new List<string>();
        int filesRead = 0;
        foreach (string folder in folders)
        {
            Assert.IsTrue(Directory.Exists(folder), "Expected folder not found: " + folder);
            foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // this test's own regex source is not fixture data
                }

                filesRead++;
                ScanFile(file, found);
            }
        }

        Assert.IsTrue(filesRead >= 10, "Too few files were read for a clean result to mean anything: " + filesRead);
        Assert.AreEqual(0, found.Count, "A fixture looks like it came from a real device:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    // Round 2, three planted examples the prior version of this test missed:
    [TestMethod]
    public void AUlongHexLiteralWithATrailingULSuffixIsCaught()
    {
        Assert.IsTrue(UlongHexLiteral.IsMatch("0x1234567890ABUL"), "A 0x...UL ulong literal must be caught.");
    }

    [TestMethod]
    public void ARealShapedHeaderPrependedToTheAllowedFillerIsNotWavedThrough()
    {
        string headerPlusFiller = "0107013344" + "101112131415161718191A1B1C1D1E1F";
        Assert.IsFalse(IsAllowedRun(headerPlusFiller), "A real-shaped header before the filler must not be allowed.");
    }

    [TestMethod]
    public void ADecimalByteArrayShapedLikeASixteenBytePayloadIsCaught()
    {
        string text = "var payload = new byte[] { 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 };";
        var found = new List<string>();

        ScanText("Plant.cs", text, found);

        Assert.IsTrue(found.Count > 0, "A decimal byte array shaped like a 16-byte payload must be caught.");
    }

    private static void ScanFile(string file, List<string> found) =>
        ScanText(Path.GetFileName(file), File.ReadAllText(file), found);

    // Pure (no file I/O), so a planted example can be run through it directly rather than only ever through
    // a file on disk under src\Earshot\Widget or tests\Earshot.Tests\Widget.
    internal static void ScanText(string name, string text, List<string> found)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (SixGroupHexAddress.IsMatch(line) || SeparatedHexRun.IsMatch(line))
            {
                found.Add(name + ":" + (i + 1) + ": looks like a Bluetooth address: " + line.Trim());
            }

            foreach (Match m in TwelveDigitHexAddress.Matches(line))
            {
                if (!IsAllowedRun(m.Value))
                {
                    found.Add(name + ":" + (i + 1) + ": a 12-digit address with no separator: " + line.Trim());
                }
            }

            if (UlongHexLiteral.IsMatch(line))
            {
                found.Add(name + ":" + (i + 1) + ": a ulong-shaped address literal: " + line.Trim());
            }

            if (line.Contains("Find My", StringComparison.Ordinal))
            {
                found.Add(name + ":" + (i + 1) + ": names \"Find My\": " + line.Trim());
            }
        }

        foreach (Match m in LongHexRun.Matches(text))
        {
            if (IsAllowedRun(m.Value))
            {
                continue;
            }

            int lineNumber = CountLines(text, m.Index);
            found.Add(name + ":" + lineNumber + ": a hex run of 16 bytes or more: " + Truncate(m.Value));
        }

        foreach (Match m in LongDecimalByteRun.Matches(text))
        {
            int lineNumber = CountLines(text, m.Index);
            found.Add(name + ":" + lineNumber + ": a decimal byte run of 16 bytes or more: " + Truncate(m.Value));
        }
    }

    private static int CountLines(string text, int upToIndex)
    {
        int lines = 1;
        for (int i = 0; i < upToIndex && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    // Exact match only, not Contains: a real-shaped header concatenated directly onto one of these fillers
    // would otherwise be waved through just because the filler's own digits appear somewhere inside the
    // combined run, hiding exactly the kind of real capture this test exists to catch.
    private static bool IsAllowedRun(string match)
    {
        string normalized = new string(match.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return AllowedSyntheticRuns.Contains(normalized, StringComparer.Ordinal);
    }

    private static string Truncate(string value) => value.Length > 60 ? value[..60] + "..." : value;

    private static string FindRepoRoot(string thisFile)
    {
        DirectoryInfo? dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "Could not find the repository root (Earshot.slnx) above " + thisFile);
        return dir!.FullName;
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

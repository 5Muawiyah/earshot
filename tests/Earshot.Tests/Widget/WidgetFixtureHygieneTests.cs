using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Fixtures, and the code that reads them, are synthetic only. Scans every .cs file under
// src\Earshot\Widget, tests\Earshot.Tests\Widget and src\Earshot.AdvertProbe, plus src\Earshot\App\ProbeWidget.cs
// (including WidgetFixtures.cs itself, unlike the prior version of this test) for anything shaped like a
// real device: a six-group colon- or dash-separated Bluetooth address, any shorter colon/dash-separated hex
// run, a bare 12-hex-digit address with no separator, a ulong-shaped hex literal (upper or lower case 0x,
// digit separators and a UL/LU suffix all allowed, since C# allows them in the literal itself), a plain hex
// payload of 8 bytes or more written as 0x.. tokens (each optionally cast, (byte)0x..), as bare hex digits
// with no separator at all, or as bare hex byte pairs separated only by spaces, a decimal byte run of 8 or
// more, a base64 run shaped like a payload, and the words "Find My" (checked across the whole file, not line
// by line, so a run split by a formatter across several lines is still caught, and lowered from a 16-byte to
// an 8-byte threshold so a real payload split across two shorter declarations to duck a 16-byte minimum is
// still caught in each half). Also caught: 0x_ digit separators straight after the prefix, six hex pairs
// joined by colons, dashes, dots, underscores or single spaces, runs of \x escapes, and a payload cut into
// short byte lists (two 4-byte arrays, two 7-byte ones) that add up to eight bytes or more. The only byte
// runs let through are the fixture's own documented synthetic ones, listed explicitly: the 0x10..0x1F and
// 0x20..0x2F fillers.
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

    // 0x or 0X followed by 12 or more hex digits (C#'s own optional digit separator, underscore, allowed
    // between them, so 0x1234_5678_9ABC is not a different shape from 0x123456789ABC): a 48-bit Bluetooth
    // address packed into a ulong needs exactly 12, so this starts above the width of an ordinary 8-digit
    // HRESULT or CONFIGRET literal (0x80004005 and the like are routine in this codebase and are not
    // addresses). An optional UL/LU-style C# integer literal suffix is consumed too, itself allowed a
    // digit-separator split (0x..._...UL): \b does not sit between two word characters, so "...ABUL" would
    // otherwise never reach a boundary right after the hex digits and the literal would go uncaught.
    // C# also allows separators straight after the prefix (0x_1234_5678_9ABC), so the prefix is followed by
    // any number of underscores before the first digit.
    private static readonly Regex UlongHexLiteral = new(
        @"\b0[xX]_*(?:[0-9A-Fa-f]_*){12,}(?:_*[uU]_*[lL]?|_*[lL]_*[uU]?)?\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Six hex pairs joined by any mix of colon, dash, dot, underscore or a single space: an address written
    // in a form SixGroupHexAddress (colon and dash only, with \b anchors that an underscore defeats) does not
    // cover. Anchored by "not next to a letter or digit" rather than \b, since an underscore is a word
    // character and would otherwise hide a token that starts after one.
    private static readonly Regex SixPairAnySeparator = new(
        @"(?<![0-9A-Za-z])(?:[0-9A-Fa-f]{2}[:._\- ]){5}[0-9A-Fa-f]{2}(?![0-9A-Za-z])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Four or more \x escapes in a row in a string literal: a payload written as escapes, with one to four
    // hex digits each (C# allows that many).
    private static readonly Regex BackslashXRun = new(
        @"(?:\\x[0-9A-Fa-f]{1,4}){4,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A brace-enclosed list made only of byte-sized numbers, hex (digit separators allowed, an optional
    // (byte) cast) or decimal: the body of a byte array initialiser, whatever precedes the brace, as long as
    // whitespace does. That last condition keeps a regular expression's own quantifier ({5,} or {1,2}, which
    // this file and others write inside string literals, always straight after the thing it repeats) from
    // being read as a list of two byte values.
    private static readonly Regex ByteListInitialiser = new(
        @"(?<=\s)\{\s*(?<items>(?:(?:\(byte\)\s*)?(?:0[xX]_*[0-9A-Fa-f]_*[0-9A-Fa-f]?|\d{1,3})\s*,?\s*)+)\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    // How far apart (in characters, between one closing brace and the next opening one) two byte lists may
    // be and still read as one payload cut into pieces: a statement or two of glue, not a page.
    private const int MaxGapBetweenPayloadPieces = 200;

    // 16 or more hex byte literals in a row (a captured payload's shape), each optionally cast ((byte)0x..,
    // the shape a byte[] initialiser sometimes needs) and ignoring the punctuation a byte array initialiser
    // uses; a bare run of 14 or more hex digits with no separator at all (an address or header shape shorter
    // than a full 16-byte payload - TwelveDigitHexAddress only ever matches exactly 12 with a boundary right
    // after, so a 9-byte, 18-digit header packed with no separator falls through it); or 16 or more
    // space-separated bare hex byte pairs with no 0x prefix at all. Applied to the whole file so a run a
    // formatter split across lines is still one match, not sixteen separate two-digit non-matches, and
    // lowered from a 16-byte to an 8-byte threshold on the punctuated and bare-run shapes so a real payload
    // split across two shorter declarations to duck a 16-byte minimum is still caught in each half.
    private static readonly Regex LongHexRun = new(
        @"(?:(?:\(byte\)\s*)?0[xX]_*[0-9A-Fa-f]_*[0-9A-Fa-f][,\s]*){8,}|[0-9A-Fa-f]{14,}|(?:\b[0-9A-Fa-f]{2}\b[,\s]+){7,}\b[0-9A-Fa-f]{2}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    // 8 or more decimal byte values (0-255) in a row, comma- or whitespace-separated: the same captured-
    // payload shape as LongHexRun, written in decimal instead of hex, and lowered the same way.
    private static readonly Regex LongDecimalByteRun = new(
        @"(?:\b(?:25[0-5]|2[0-4]\d|1\d\d|\d\d?)\b[,\s]+){7,}\b(?:25[0-5]|2[0-4]\d|1\d\d|\d\d?)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    // A base64-encoded run shaped like a real captured payload, written as a C# string literal: at least 20
    // characters from the base64 alphabet (at least 15 bytes decoded), optionally followed by 1 or 2 "="
    // padding characters, and at least one digit somewhere in it. Anchored to the quotes, not \b...\b: an
    // ordinary PascalCase method, type or member name is also, incidentally, a run of letters from the
    // base64 alphabet, sometimes 20 characters or longer, and this codebase quotes such a name in a string
    // literal often enough ("InvalidOperationException", "LowBatteryThresholdPercent") that the quotes
    // alone are not a strong enough filter. The digit requirement is: an English identifier this codebase
    // writes essentially never contains one, while a real base64 run of anything but the shortest payload
    // is built from an alphabet that is 10/64 digits, so one appearing somewhere in 20+ characters is the
    // ordinary case, not the exception.
    private static readonly Regex Base64Run = new(
        @"""(?=[A-Za-z0-9+/]*[0-9])[A-Za-z0-9+/]{20,}={0,2}""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        // Widened: the phase 0 probe (src\Earshot.AdvertProbe) and the widget's own probe capture code
        // (ProbeWidget.cs, under src\Earshot\App rather than src\Earshot\Widget) both build the same kind
        // of synthetic fixtures the two folders below were already checked for.
        string[] folders =
        {
            Path.Combine(repoRoot, "src", "Earshot", "Widget"),
            Path.Combine(repoRoot, "tests", "Earshot.Tests", "Widget"),
            Path.Combine(repoRoot, "src", "Earshot.AdvertProbe"),
        };
        string[] extraFiles =
        {
            Path.Combine(repoRoot, "src", "Earshot", "App", "ProbeWidget.cs"),
        };

        var found = new List<string>();
        int filesRead = 0;
        foreach (string folder in folders)
        {
            Assert.IsTrue(Directory.Exists(folder), "Expected folder not found: " + folder);
            foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // this test's own regex source is not fixture data; build output is not source
                }

                filesRead++;
                ScanFile(file, found);
            }
        }

        foreach (string file in extraFiles)
        {
            Assert.IsTrue(File.Exists(file), "Expected file not found: " + file);
            filesRead++;
            ScanFile(file, found);
        }

        Assert.IsTrue(filesRead >= 10, "Too few files were read for a clean result to mean anything: " + filesRead);
        Assert.AreEqual(0, found.Count, "A fixture looks like it came from a real device:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    // Three planted examples the prior version of this test missed:
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

    // Seven planted examples the widened version of this test now catches, each a shape the prior version
    // missed outright.
    [TestMethod]
    public void AnUppercaseZeroXUlongLiteralIsCaught()
    {
        Assert.IsTrue(UlongHexLiteral.IsMatch("0X1234567890ABCD"), "An uppercase 0X ulong literal must be caught.");
    }

    [TestMethod]
    public void SpaceSeparatedHexBytesWithNoZeroXPrefixAreCaught()
    {
        string text = "var payload = \"01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10\";";
        var found = new List<string>();

        ScanText("Plant.cs", text, found);

        Assert.IsTrue(found.Count > 0, "Space-separated bare hex byte pairs must be caught.");
    }

    [TestMethod]
    public void ABase64RunShapedLikeAPayloadIsCaught()
    {
        string text = "var payload = \"AQIDBAUGBwgJCgsMDQ4PEA==\";"; // 16 bytes, 0x01..0x10, base64-encoded
        var found = new List<string>();

        ScanText("Plant.cs", text, found);

        Assert.IsTrue(found.Count > 0, "A base64 run shaped like a payload must be caught.");
    }

    [TestMethod]
    public void ANineByteRealShapedHeaderWithNoSeparatorIsCaught()
    {
        // 9 bytes, 18 hex digits: shorter than the 32-digit bare-run threshold this test used to need, and
        // not exactly 12 digits either, so it used to fall between every check this test had.
        string text = "var header = \"010701334401020304\";";
        var found = new List<string>();

        ScanText("Plant.cs", text, found);

        Assert.IsTrue(found.Count > 0, "A 9-byte header with no separator must be caught.");
    }

    [TestMethod]
    public void ByteCastHexLiteralsAreCaught()
    {
        string text = "var payload = new byte[] { (byte)0x10, (byte)0x11, (byte)0x12, (byte)0x13, (byte)0x14, " +
            "(byte)0x15, (byte)0x16, (byte)0x17 };";
        var found = new List<string>();

        ScanText("Plant.cs", text, found);

        Assert.IsTrue(found.Count > 0, "A byte[] of (byte)0x.. casts must be caught.");
    }

    [TestMethod]
    public void ADigitSeparatedUlongLiteralIsCaught()
    {
        Assert.IsTrue(UlongHexLiteral.IsMatch("0x1234_5678_9ABCUL"), "A digit-separated 0x..._...UL literal must be caught.");
    }

    [TestMethod]
    public void APayloadSplitAcrossTwoEightByteArraysIsCaughtInEachHalf()
    {
        string text = "var first = new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17 };\n" +
            "var second = new byte[] { 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F };";
        var found = new List<string>();

        ScanText("Plant.cs", text, found);

        Assert.AreEqual(2, found.Count, "Each 8-byte half of a payload split across two declarations must be caught on its own.");
    }

    // Shapes the scan above still let through, each planted here as a sample that must be reported. Every one
    // is a way to write a device address or a captured payload that a fixture author could reach for
    // without meaning to hide anything.

    // C# lets an underscore follow the 0x prefix itself, before the first digit.
    [TestMethod]
    public void AUlongLiteralWithADigitSeparatorRightAfterTheZeroXIsCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs", "ulong address = 0x_1234_5678_9ABC;", found);

        Assert.IsTrue(found.Count > 0, "0x_ followed by 12 digits must be caught.");
        Assert.IsTrue(UlongHexLiteral.IsMatch("0x_1234_5678_9ABCUL"), "The suffixed form must be caught too.");
    }

    [TestMethod]
    public void ByteLiteralsWithDigitSeparatorsAreCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs",
            "var payload = new byte[] { 0x_10, 0x_11, 0x_12, 0x_13, 0x_14, 0x_15, 0x_16, 0x_17 };", found);

        Assert.IsTrue(found.Count > 0, "Eight 0x_.. byte literals must be caught.");
    }

    [TestMethod]
    [DataRow("AA_BB_CC_DD_EE_FF", "underscore separated")]
    [DataRow("AA.BB.CC.DD.EE.FF", "dot separated")]
    [DataRow("aa_bb_cc_dd_ee_ff", "lower-case underscore separated")]
    [DataRow("AA:BB-CC_DD.EE:FF", "mixed separators")]
    public void ASixPairAddressWithUnderscoresOrDotsIsCaught(string address, string shape)
    {
        var found = new List<string>();

        ScanText("Plant.cs", "var address = \"" + address + "\";", found);

        Assert.IsTrue(found.Count > 0, "A six-pair address, " + shape + ", must be caught.");
    }

    [TestMethod]
    public void AHexPayloadWrittenAsBackslashXEscapesIsCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs", "var payload = \"\\x10\\x11\\x12\\x13\\x14\\x15\";", found);

        Assert.IsTrue(found.Count > 0, "Six \\x.. escapes in a row must be caught.");
    }

    [TestMethod]
    public void ASixPairAddressSeparatedOnlyBySpacesIsCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs", "var address = \"AA BB CC DD EE FF\";", found);

        Assert.IsTrue(found.Count > 0, "Six space-separated hex pairs must be caught.");
    }

    // A payload cut into pieces short enough to slip under the eight-byte threshold in each piece: two
    // 4-byte arrays, and two 7-byte arrays, in hex and in decimal. The pieces are reported as a run.
    [TestMethod]
    public void APayloadSplitIntoFourByteHexArraysIsCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs",
            "var first = new byte[] { 0x10, 0x11, 0x12, 0x13 };\nvar second = new byte[] { 0x14, 0x15, 0x16, 0x17 };", found);

        Assert.IsTrue(found.Count > 0, "Two 4-byte arrays that together make 8 bytes must be caught.");
    }

    [TestMethod]
    public void APayloadSplitIntoSevenByteDecimalArraysIsCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs",
            "byte[] a = { 16, 17, 18, 19, 20, 21, 22 };\nbyte[] b = { 23, 24, 25, 26, 27, 28, 29 };", found);

        Assert.IsTrue(found.Count > 0, "Two 7-byte decimal arrays must be caught.");
    }

    [TestMethod]
    public void APayloadSplitIntoManySmallCastArraysIsCaught()
    {
        var found = new List<string>();

        ScanText("Plant.cs",
            "var parts = new[] { new byte[] { (byte)0x10, (byte)0x11 }, new byte[] { (byte)0x12, (byte)0x13 }, " +
            "new byte[] { (byte)0x14, (byte)0x15 }, new byte[] { (byte)0x16, (byte)0x17 } };", found);

        Assert.IsTrue(found.Count > 0, "Four 2-byte arrays that together make 8 bytes must be caught.");
    }

    // The runs above must not turn ordinary small arrays into findings: one short array, two that are far
    // apart, and a repeated default value are all fine.
    [TestMethod]
    public void OrdinarySmallByteArraysAreNotReported()
    {
        var found = new List<string>();

        ScanText("Plant.cs", "var one = new byte[] { 0x01, 0x02, 0x03, 0x04 };", found);
        ScanText("Plant.cs",
            "var a = new byte[] { 1, 2, 3, 4 };\n" + string.Concat(Enumerable.Repeat("// unrelated commentary line\n", 20)) +
            "var b = new byte[] { 5, 6, 7, 8 };", found);
        ScanText("Plant.cs", "var zeros = new byte[] { 0, 0, 0, 0 }; var more = new byte[] { 0, 0, 0, 0 };", found);

        Assert.AreEqual(0, found.Count, string.Join(Environment.NewLine, found));
    }

    private static void ScanFile(string file, List<string> found) =>
        ScanText(Path.GetFileName(file), File.ReadAllText(file), found);

    // src\Earshot.AdvertProbe is a whole project root, unlike the two Widget subfolders scanned alongside
    // it (their own project's bin/obj live above them, never inside), so a clean build's own generated
    // Earshot.AdvertProbe.AssemblyInfo.cs (SourceRevisionId: the full 40-hex current git commit, baked in by
    // the SDK's own SourceLink support) lands squarely in its scan and reads exactly like a Bluetooth
    // address's hex run. The same exclusion NoSpecCitationsTests already uses for its own two, differently
    // shaped trees.
    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase);
    }

    // Pure (no file I/O), so a planted example can be run through it directly rather than only ever through
    // a file on disk under src\Earshot\Widget or tests\Earshot.Tests\Widget.
    internal static void ScanText(string name, string text, List<string> found)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (SixGroupHexAddress.IsMatch(line) || SeparatedHexRun.IsMatch(line) || SixPairAnySeparator.IsMatch(line))
            {
                found.Add(name + ":" + (i + 1) + ": looks like a Bluetooth address: " + line.Trim());
            }

            if (BackslashXRun.IsMatch(line))
            {
                found.Add(name + ":" + (i + 1) + ": a run of \\x escapes shaped like a payload: " + line.Trim());
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
            found.Add(name + ":" + lineNumber + ": a hex run shaped like an address, a header or a payload: " + Truncate(m.Value));
        }

        foreach (Match m in LongDecimalByteRun.Matches(text))
        {
            // An ordinary default-value initialiser (a record or struct constructed with the same small
            // constant repeated for every field, WidgetCounters.Empty being the real example that found
            // this) is not a captured payload: a real one varies, so an all-identical run is not reported.
            if (IsAllTheSameValue(m.Value))
            {
                continue;
            }

            int lineNumber = CountLines(text, m.Index);
            found.Add(name + ":" + lineNumber + ": a decimal byte run of 8 bytes or more: " + Truncate(m.Value));
        }

        foreach (Match m in Base64Run.Matches(text))
        {
            int lineNumber = CountLines(text, m.Index);
            found.Add(name + ":" + lineNumber + ": a base64 run shaped like a captured payload: " + Truncate(m.Value));
        }

        ScanCutUpPayloads(name, text, found);
    }

    // A payload cut into pieces that each stay under the eight-byte threshold (two 4-byte arrays, two 7-byte
    // ones, four 2-byte ones) is one payload however it is declared. Lists of byte-sized numbers that follow
    // each other within MaxGapBetweenPayloadPieces are added up, and a run of two or more pieces reaching
    // eight bytes is reported. A list that is itself eight bytes or more is left to the scans above, so it is
    // never reported twice; a run in which every number is the same is a default value, not a capture.
    private static void ScanCutUpPayloads(string name, string text, List<string> found)
    {
        var pieces = new List<(int Start, int End, List<int> Values)>();
        foreach (Match m in ByteListInitialiser.Matches(text))
        {
            List<int>? values = ParseByteList(m.Groups["items"].Value);
            if (values is not null && values.Count is > 0 and < 8)
            {
                pieces.Add((m.Index, m.Index + m.Length, values));
            }
        }

        int runStart = 0;
        while (runStart < pieces.Count)
        {
            int runEnd = runStart;
            var all = new List<int>(pieces[runStart].Values);
            while (runEnd + 1 < pieces.Count && pieces[runEnd + 1].Start - pieces[runEnd].End <= MaxGapBetweenPayloadPieces)
            {
                runEnd++;
                all.AddRange(pieces[runEnd].Values);
            }

            if (runEnd > runStart && all.Count >= 8 && all.Distinct().Count() > 1)
            {
                int lineNumber = CountLines(text, pieces[runStart].Start);
                found.Add(name + ":" + lineNumber + ": " + (runEnd - runStart + 1) + " short byte lists that together make " +
                    all.Count + " bytes, a payload cut into pieces");
            }

            runStart = runEnd + 1;
        }
    }

    // The byte values in a brace body, or null when any item is not a number from 0 to 255.
    private static List<int>? ParseByteList(string items)
    {
        var values = new List<int>();
        foreach (string raw in items.Split(','))
        {
            string item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (item.StartsWith("(byte)", StringComparison.Ordinal))
            {
                item = item[6..].Trim();
            }

            bool parsed;
            int value;
            if (item.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                parsed = int.TryParse(item[2..].Replace("_", "", StringComparison.Ordinal), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            }
            else
            {
                parsed = int.TryParse(item, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
            }

            if (!parsed || value is < 0 or > 255)
            {
                return null;
            }

            values.Add(value);
        }

        return values;
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

    private static readonly char[] RunSeparators = { ',', ' ', '\t', '\r', '\n' };

    // True when every number in a comma/whitespace-separated run is the same value: an ordinary repeated
    // default (0, 0, 0, ...), never a real captured payload.
    private static bool IsAllTheSameValue(string run)
    {
        string[] parts = run.Split(RunSeparators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.Distinct(StringComparer.Ordinal).Count() == 1;
    }

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

using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Fixtures are synthetic only. Scans every file in this folder except WidgetFixtures.cs itself for a
// six-group hex Bluetooth address, a hex run of 16 bytes or more (the shape of a captured payload), and the
// words "Find My": nothing from a real device, and nothing read from %LOCALAPPDATA%\Earshot\phase0, ever
// lands in a test fixture.
[TestClass]
public sealed class WidgetFixtureHygieneTests
{
    // Six colon- or dash-separated hex pairs: the documented Bluetooth address text form.
    private static readonly Regex SixGroupHexAddress = new(
        @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 32 or more hex digits in a row, ignoring the punctuation a byte array literal uses (0x, commas,
    // spaces): the shape of a captured 16-byte-or-longer payload.
    private static readonly Regex LongHexRun = new(@"(?:0x[0-9A-Fa-f]{2}[,\s]*){16,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [TestMethod]
    public void NoFixtureHoldsAnAddressACapturedPayloadOrAName()
    {
        string thisFile = Path.GetFullPath(ThisFilePath());
        string folder = Path.GetDirectoryName(thisFile)!;
        Assert.IsTrue(Directory.Exists(folder), "The widget test folder was not found: " + folder);

        var found = new List<string>();
        int filesRead = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileName(file), "WidgetFixtures.cs", StringComparison.Ordinal) ||
                string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filesRead++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (SixGroupHexAddress.IsMatch(line))
                {
                    found.Add(Path.GetFileName(file) + ":" + (i + 1) + ": looks like a Bluetooth address: " + line.Trim());
                }

                if (LongHexRun.IsMatch(line))
                {
                    found.Add(Path.GetFileName(file) + ":" + (i + 1) + ": a hex run of 16 bytes or more: " + line.Trim());
                }

                if (line.Contains("Find My", StringComparison.Ordinal))
                {
                    found.Add(Path.GetFileName(file) + ":" + (i + 1) + ": names \"Find My\": " + line.Trim());
                }
            }
        }

        Assert.IsTrue(filesRead >= 10, "Too few files were read in " + folder + " for a clean result to mean anything: " + filesRead);
        Assert.AreEqual(0, found.Count, "A fixture looks like it came from a real device:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

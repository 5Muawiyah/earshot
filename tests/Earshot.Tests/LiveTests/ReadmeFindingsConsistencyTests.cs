using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// README.md promises that a named finding, backticked and camelCase, will be there in
// result.json after a given live test script runs. Nothing here runs a script; it reads the
// README's own words and the scripts' own Add-Finding calls and checks the promise against the
// source, so a promise that drifts from what a script actually writes is caught without anyone
// running the test by hand and discovering the name is not there.
//
// The README has only one place that carries a finding name in a form worth extracting on its
// own: the paragraph explaining how Fast Startup is covered by hand, which names
// `fastStartupAtPowerDown` and offers running "08 (or 09)" to get it. Nothing else in the file
// backticks a camelCase name, so scoping the extraction to that paragraph (rather than the whole
// file) is what keeps this from flagging an ordinary code identifier elsewhere in the prose.
[TestClass]
public sealed class ReadmeFindingsConsistencyTests
{
    // A backticked identifier that reads as camelCase: starts lower-case, holds at least one
    // upper-case letter later on. That shape is what Add-Finding's -Name values look like
    // (secondsFromIdleToBlock, fastStartupAtPowerDown, ...); it excludes a plain lower-case word
    // like `install` or `diag`, which the README also backticks but which are commands, not
    // findings.
    private static readonly Regex CamelCaseBacktick = new(@"`([a-z][a-zA-Z0-9]*)`", RegexOptions.Compiled);

    // A live test script mentioned by its two-digit number, in a context that reads as "run this
    // test": "Tests 08 and 09", "run 08 or 09", and similar. Loose on purpose: this file is
    // read-only evidence, and a paragraph that is rewritten to talk about the tests differently
    // should still be picked up rather than silently stop being checked.
    private static readonly Regex ScriptNumberMention = new(@"\b(0[0-9]|1[0-5])\b", RegexOptions.Compiled);

    // The precise pairing README.md uses when it ties one finding to one specific test, for
    // example "`fastStartupAtPowerDown` from test 08". When the paragraph spells it out this
    // exactly, that pairing is what gets checked, rather than every name against every number the
    // paragraph happens to mention.
    private static readonly Regex NameFromTestPairing = new(@"`([a-zA-Z][a-zA-Z0-9]*)`\s+from test\s+(0[0-9]|1[0-5])", RegexOptions.Compiled);

    [TestMethod]
    public void FastStartupFindingIsWrittenByEveryScriptTheReadmeNamesForIt()
    {
        var readmeText = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        var paragraph = FindFastStartupParagraph(readmeText);
        Assert.IsNotNull(
            paragraph,
            "README.md no longer has the paragraph that explains how Fast Startup is covered by hand " +
            "(the one that used to name `fastStartupAtPowerDown`). This test needs updating to read " +
            "wherever that explanation moved to.");

        var findingNames = CamelCaseBacktick.Matches(paragraph!)
            .Select(m => m.Groups[1].Value)
            .Where(name => name.Skip(1).Any(char.IsUpper))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.IsNotEmpty(
            findingNames,
            "The Fast Startup paragraph in README.md no longer backticks a camelCase finding name. " +
            "Either the paragraph changed shape (update this test to match) or the promise it made was " +
            "removed, which is fine, but then this test has nothing left to check.");

        // "`name` from test NN" pairs a finding with exactly the script it says writes it. When
        // the paragraph is that explicit, only those pairs are checked. Otherwise every name
        // backticked in the paragraph is checked against every script number the paragraph
        // mentions, which is the loose, conservative reading that still fails when the paragraph
        // promises a name a script never writes.
        var pairs = NameFromTestPairing.Matches(paragraph!)
            .Select(m => (Name: m.Groups[1].Value, Number: m.Groups[2].Value))
            .ToList();

        if (pairs.Count == 0)
        {
            var scriptNumbers = ScriptNumberMention.Matches(paragraph!)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.IsNotEmpty(
                scriptNumbers,
                "The Fast Startup paragraph in README.md no longer names any test by number, so there is " +
                "nothing to check the finding names against.");

            pairs = findingNames
                .SelectMany(name => scriptNumbers.Select(number => (Name: name, Number: number)))
                .ToList();
        }

        var scriptsFolder = Path.Combine(RepositoryRoot(), "tools", "live-tests");
        var problems = new List<string>();

        foreach (var (name, number) in pairs)
        {
            var scriptPath = Directory.EnumerateFiles(scriptsFolder, number + "-*.ps1").SingleOrDefault();
            if (scriptPath is null)
            {
                problems.Add("README.md names test " + number + ", and there is no " + number + "-*.ps1 under tools\\live-tests.");
                continue;
            }

            var scriptText = File.ReadAllText(scriptPath);
            var scriptFileName = Path.GetFileName(scriptPath);
            var written = Regex.IsMatch(
                scriptText,
                @"Add-Finding\s+-Run\s+\$run\s+-Name\s+'" + Regex.Escape(name) + @"'");
            if (!written)
            {
                problems.Add(
                    "README.md says running test " + number + " lets you read `" + name + "` back, but " +
                    scriptFileName + " never calls Add-Finding -Name '" + name + "'.");
            }
        }

        Assert.IsEmpty(
            problems,
            "A finding README.md promises is not written by a script the same sentence names for it:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    // The paragraph is found by its own distinctive sentence rather than by a line number, so a
    // README edit that moves it (without changing its wording) does not silently stop being
    // checked. Paragraphs are split on a blank line, which is how the rest of README.md is
    // written.
    private static string? FindFastStartupParagraph(string readmeText)
    {
        var paragraphs = Regex.Split(readmeText, @"\r?\n\s*\r?\n");
        return paragraphs.FirstOrDefault(p => p.Contains("settles Fast Startup", StringComparison.Ordinal));
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

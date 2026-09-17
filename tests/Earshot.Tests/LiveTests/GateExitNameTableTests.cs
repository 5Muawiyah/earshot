using System.Globalization;
using System.Text.RegularExpressions;
using Earshot.Boot.Gate;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// Get-GateExitName in tools\live-tests\LiveTest.psm1 turns the exit code of install, uninstall and
// the gate into the name the application's own log uses. That name is written into result.json for
// every step, into the detail of the uninstall criteria and into the recorded installExit finding,
// so a wrong name sends the owner after the wrong remedy during a live sitting.
//
// The module cannot reference the application, so the table is copied there by hand. These tests
// hold the copy to GateExitCode, value for value, so the two can never drift apart again.
[TestClass]
public sealed class GateExitNameTableTests
{
    // Codes the module names that are not GateExitCode values: the sysexits values Program returns
    // for a command line it will not run, and the Windows code for a declined administrator prompt.
    // They are listed here so an entry that is neither a gate code nor one of these fails the test.
    private static readonly Dictionary<int, string> OtherCodes = new()
    {
        [64] = "bad command line",                            // EX_USAGE
        [69] = "not available in this build",                 // EX_UNAVAILABLE
        [77] = "refused",                                     // EX_NOPERM
        [1223] = "the administrator prompt was declined",     // ERROR_CANCELLED
    };

    [TestMethod]
    public void EveryGateExitCodeIsNamedTheWayTheApplicationNamesIt()
    {
        Dictionary<int, string> table = ReadTable();
        var problems = new List<string>();
        foreach (GateExitCode code in Enum.GetValues<GateExitCode>())
        {
            int number = (int)code;
            string expected = GateExitCodes.ResultName(code);
            if (!table.TryGetValue(number, out string? actual))
            {
                problems.Add(Text(number) + " (" + expected + ") is missing from Get-GateExitName.");
            }
            else if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                problems.Add(Text(number) + " is named '" + actual + "' in Get-GateExitName but '" + expected + "' by the application.");
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void TheTableNamesNothingTheApplicationDoesNotReturn()
    {
        Dictionary<int, string> table = ReadTable();
        var problems = new List<string>();
        foreach ((int number, string name) in table)
        {
            if (OtherCodes.TryGetValue(number, out string? other))
            {
                if (!string.Equals(name, other, StringComparison.Ordinal))
                {
                    problems.Add(Text(number) + " is named '" + name + "' but this test expects '" + other + "'.");
                }

                continue;
            }

            string? applicationName = GateExitCodes.NameOf(number);
            if (applicationName is null)
            {
                problems.Add(Text(number) + " is named '" + name + "' in Get-GateExitName, but no gate mode returns it.");
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    // The sysexits values Program uses must stay clear of the gate codes, or one number would want
    // two names and the table could not be right for both.
    [TestMethod]
    public void TheOtherCodesDoNotCollideWithTheGateCodes()
    {
        foreach (int number in OtherCodes.Keys)
        {
            Assert.IsNull(
                GateExitCodes.NameOf(number),
                "Exit code " + Text(number) + " is both a gate code and one of the others, so it cannot be named once.");
        }
    }

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    // The $names hashtable literal inside Get-GateExitName, read as number-to-name pairs. Reading
    // the text rather than running the module keeps this test off PowerShell entirely.
    private static Dictionary<int, string> ReadTable()
    {
        string path = Path.Combine(RepositoryRoot(), "tools", "live-tests", "LiveTest.psm1");
        Assert.IsTrue(File.Exists(path), path + " is missing.");
        string module = File.ReadAllText(path);

        int start = module.IndexOf("function Get-GateExitName", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, "Get-GateExitName was not found in LiveTest.psm1.");

        int open = module.IndexOf("$names = @{", start, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, open, "The $names table was not found in Get-GateExitName.");

        int close = module.IndexOf('}', open);
        Assert.IsGreaterThanOrEqualTo(0, close, "The $names table is not closed.");

        string body = module[open..close];
        var table = new Dictionary<int, string>();
        foreach (Match match in Regex.Matches(body, @"(?<number>\d+)\s*=\s*'(?<name>[^']*)'", RegexOptions.None, TimeSpan.FromSeconds(5)))
        {
            int number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            Assert.IsFalse(table.ContainsKey(number), "Exit code " + Text(number) + " is listed twice in Get-GateExitName.");
            table[number] = match.Groups["name"].Value;
        }

        Assert.IsGreaterThan(0, table.Count, "No entry was read out of the $names table, so this test proves nothing.");
        return table;
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

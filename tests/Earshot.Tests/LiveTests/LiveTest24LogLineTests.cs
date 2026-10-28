using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The log lines live test 24 reads, as the regular expressions the script matches them with. Each is checked against the script
// here, and against a line the real flow writes in the test of that flow, so a reworded line fails a test here instead of the
// owner's run on the PC. The self-test's fake lines are typed by hand and would not notice.
internal static class LiveTest24Lines
{
    public const string Started = @"Pair AirPods: started from the menu\.";
    public const string Paired = @"Pair AirPods: Paired, None\.";
    public const string Adopted = @"Pair AirPods: the new pair is the one Earshot uses\.";
    public const string Answered = @"pair: Windows answered (Paired|AlreadyPaired) \(-?[0-9]+\)\.";
    public const string RadioOk = @"radio check: Ok in [0-9]+ ms\.";
    public const string RadioOffAfterConnect = @"Radio check after a failed connect, Off in [0-9]+ ms\.";
    public const string TraceStarted = @"Bluetooth trace: started; ";
    public const string TraceSaved = @"Bluetooth trace ended: Saved\.";

    public static readonly string[] All = [Started, Paired, Adopted, Answered, RadioOk, RadioOffAfterConnect, TraceStarted, TraceSaved];

    public static void AssertLogged(CapturingLog log, string pattern)
    {
        ArgumentNullException.ThrowIfNull(log);
        Assert.IsTrue(log.Entries.Any(e => Regex.IsMatch(e.Message, pattern, RegexOptions.CultureInvariant)),
            "Live test 24 reads a line matching " + pattern + ", and none was written: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
    }
}

[TestClass]
public sealed class LiveTest24LogLineTests
{
    [TestMethod]
    public void EachLineTheTestsPinIsTheOneTheScriptMatches()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "live-tests", "24-PairAirPods.ps1"));

        foreach (string pattern in LiveTest24Lines.All)
        {
            StringAssert.Contains(script, "-Regex '" + pattern + "'", "24-PairAirPods.ps1 no longer matches this line; change the test of its flow with it.");
        }
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

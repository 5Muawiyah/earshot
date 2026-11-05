using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The log lines live test 25 reads, as the regular expressions the script matches them with. Each is checked against the script here,
// against a sample line the self-test writes as a fixture, and against a line the real flow writes in the test of that flow, so a
// reworded line fails a test here instead of the owner's run on the PC. The self-test's fake lines are typed by hand and would not notice.
internal static class LiveTest25Lines
{
    public const string CaseOpened = @"Case: open by (link|burst): ";
    public const string CaseCardShown = @"Card: shown unasked \(case\), ";
    public const string CaseCardClosed = @"Card: closed \(case\), ";
    public const string MovedCardShown = @"Card: shown unasked \(moved to\), ";
    public const string MovedVerdict = @"Move: [0-9]+ messages? by \+[0-9]+ ms.*: moved\.";
    public const string TakeBackPressed = @"Card: button pressed \(Take back\)\.";
    public const string FailedCard = @"Card: (shown unasked \(connect failed\), |connect failed, said in the open card)";
    public const string GaugeFigure = @"Battery: gauge [0-9]+ from (left|right), ";
    public const string ConnectShortcut = @"Switch to-pc: active after ([0-9]+|-) ms \(trigger shortcut-to-pc,";
    public const string DisconnectShortcut = @"Switch to-phone: released after ([0-9]+|-) ms, at rest after [0-9-]+ ms \(trigger shortcut-to-phone,";
    public const string NotActiveShortcut = @"Switch to-pc: not active \(outcome [A-Za-z]+, trigger shortcut-to-pc,";

    public static readonly string[] All =
    [
        CaseOpened, CaseCardShown, CaseCardClosed, MovedCardShown, MovedVerdict, TakeBackPressed, FailedCard, GaugeFigure,
        ConnectShortcut, DisconnectShortcut, NotActiveShortcut,
    ];

    public static void AssertMatches(string line, string pattern)
    {
        ArgumentNullException.ThrowIfNull(line);
        Assert.IsTrue(Regex.IsMatch(line, pattern, RegexOptions.CultureInvariant),
            "Live test 25 reads a line matching " + pattern + ", and the flow wrote: " + line);
    }

    public static void AssertLogged(CapturingLog log, string pattern)
    {
        ArgumentNullException.ThrowIfNull(log);
        Assert.IsTrue(log.Entries.Any(e => Regex.IsMatch(e.Message, pattern, RegexOptions.CultureInvariant)),
            "Live test 25 reads a line matching " + pattern + ", and none was written: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
    }
}

[TestClass]
public sealed class LiveTest25LogLineTests
{
    [TestMethod]
    public void EachLineTheTestsPinIsOneThePatternTheScriptMatches()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "live-tests", "25-OneCard.ps1"));

        foreach (string pattern in LiveTest25Lines.All)
        {
            StringAssert.Contains(script, "'" + pattern + "'", "25-OneCard.ps1 no longer matches this line; change the test of its flow with it.");
        }
    }

    [TestMethod]
    public void EachSampleLineTheSelfTestWritesMatchesItsPattern()
    {
        string fakes = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "live-tests", "selftest", "Fakes.psm1"));
        (string Line, string Pattern)[] samples =
        [
            ("Card: shown unasked (case), on 2 displays, state QUNS_ACCEPTS_NOTIFICATIONS, closes when the case closes.", LiveTest25Lines.CaseCardShown),
            ("Card: shown unasked (moved to), on 1 display, state QUNS_ACCEPTS_NOTIFICATIONS, holds 6000 ms.", LiveTest25Lines.MovedCardShown),
            ("Card: shown unasked (connect failed), on 1 display, state QUNS_ACCEPTS_NOTIFICATIONS, holds 2000 ms.", LiveTest25Lines.FailedCard),
            ("Card: closed (case), the case closed, after 8200 ms.", LiveTest25Lines.CaseCardClosed),
            ("Card: button pressed (Take back).", LiveTest25Lines.TakeBackPressed),
            ("Case: open by burst: 9 messages in 2000 ms from 2 senders, median -58 dBm, lid 0x52, no close before it.", LiveTest25Lines.CaseOpened),
            ("Move: 3 messages by +3800 ms, first +900 ms, median -62 dBm, case-known 0: moved.", LiveTest25Lines.MovedVerdict),
            ("Battery: gauge 70 from left, live.", LiveTest25Lines.GaugeFigure),
        ];

        foreach ((string line, string pattern) in samples)
        {
            LiveTest25Lines.AssertMatches(line, pattern);
            StringAssert.Contains(fakes, "Text = '" + line + "'", "Fakes.psm1 no longer writes this sample line.");
        }
    }

    [TestMethod]
    public void TheShortcutSwitchLinesTheRealFormatterWritesAreTheOnesTheScriptReads()
    {
        LiveTest25Lines.AssertMatches(RealSwitchLines.ShortcutToPc(), LiveTest25Lines.ConnectShortcut);
        LiveTest25Lines.AssertMatches(RealSwitchLines.ShortcutToPhone(), LiveTest25Lines.DisconnectShortcut);
        LiveTest25Lines.AssertMatches(RealSwitchLines.TimedOut(), LiveTest25Lines.NotActiveShortcut);
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

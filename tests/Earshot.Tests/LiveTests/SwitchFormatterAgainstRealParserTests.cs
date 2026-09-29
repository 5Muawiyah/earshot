using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Earshot.Tests.Integration.Coordinator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// tools\live-tests\selftest\Fakes.psm1 hand-writes the switch lines 16-FastSwitch.ps1 parses. A fixture that drifted
// from what the real SwitchTimelineText writes would let every self-test case pass while the real application never
// satisfied a single criterion: the self-test only proves the script against lines this repository typed by hand.
//
// This is the other half of keeping one execution of the real boundary. RealSwitchLines builds every shape with the real,
// unfaked formatter and states what each figure must read as; Test-RealSwitchLines.ps1 (tools\live-tests\selftest) takes
// the parsing functions out of the shipped script itself and runs them over those lines, over the mangled ones they must
// refuse, and over settings files whose reading is taken from Earshot's own settings serialiser, in a real PowerShell 5.1
// process through WindowsPowerShellHost. SwitchFixturesInFakesPsm1MatchTheRealFormatter pins the literal text in Fakes.psm1
// against the same formatter.
internal static class RealSwitchLines
{
    private const string AcceptedText = "2026-09-15T20:30:00.000Z";

    internal sealed record LineCase(string Label, string Line, IReadOnlyDictionary<string, object?> Expected);

    // The four lines Fakes.psm1 writes as fixtures, by the formatter. The click one took the allow-first path and the
    // shortcut one the direct path.
    public static string ClickToPc() => Line(Active(SwitchTrigger.Click, SwitchPath.AllowFirst, 12, 6, 251, 9, 1441, 3228));

    public static string ShortcutToPc() => Line(Active(SwitchTrigger.ShortcutToPc, SwitchPath.Direct, 2749, null, null, null, null, 3300));

    public static string ClickToPhone() => Line(Released(SwitchTrigger.Click, 9, 282));

    public static string ShortcutToPhone() => Line(Released(SwitchTrigger.ShortcutToPhone, 58, 291));

    // The line Fakes.psm1 writes for the switch-timed-out case.
    public static string TimedOut()
    {
        (SwitchTimeline t, ManualTime time) = Begin(connect: true, SwitchTrigger.ShortcutToPc);
        time.Advance(TimeSpan.FromMilliseconds(15012));
        t.Path = SwitchPath.AllowFirst;
        t.BlockedAgain = SwitchBlockedAgain.Yes;
        t.Complete(OpStatus.Failed, cancelled: false);
        return SwitchTimelineText.Format(t);
    }

    public static IReadOnlyList<LineCase> Lines()
    {
        var cases = new List<LineCase>();

        SwitchTimeline click = Active(SwitchTrigger.Click, SwitchPath.AllowFirst, 12, 6, 251, 9, 1441, 3228);
        cases.Add(new("to-pc active, allow-first, by click", Line(click), new Dictionary<string, object?>
        {
            ["Direction"] = "to-pc", ["Kind"] = "active", ["Trigger"] = "click", ["Path"] = "allow-first", ["ActiveMs"] = 1719, ["QueuedMs"] = 0,
            ["TotalMs"] = 4947, ["AcceptedUtc"] = AcceptedText,
            ["Phases"] = "queued 0, first-pass 12, status 6, allow 251, endpoints 9, connect 1441, protection 3228",
        }));

        SwitchTimeline direct = Active(SwitchTrigger.ShortcutToPc, SwitchPath.Direct, 2749, null, null, null, null, 3300);
        cases.Add(new("to-pc active, direct, by shortcut, phases that did not run", Line(direct), new Dictionary<string, object?>
        {
            ["Direction"] = "to-pc", ["Kind"] = "active", ["Trigger"] = "shortcut-to-pc", ["Path"] = "direct", ["ActiveMs"] = 2749, ["QueuedMs"] = 0,
            ["TotalMs"] = 6049, ["Phases"] = "queued 0, first-pass 2749, status -, allow -, endpoints -, connect -, protection 3300",
        }));

        (SwitchTimeline already, ManualTime alreadyTime) = Begin(connect: true, SwitchTrigger.ShortcutToggle);
        already.CoreBegan();
        already.Path = SwitchPath.Already;
        Phase(already, alreadyTime, SwitchPhase.FirstPass, 40);
        already.MarkActive();
        already.Complete(OpStatus.Success, cancelled: false);
        cases.Add(new("to-pc active, already, by the toggle shortcut", Line(already), new Dictionary<string, object?>
        {
            ["Kind"] = "active", ["Trigger"] = "shortcut-toggle", ["Path"] = "already", ["ActiveMs"] = 40, ["TotalMs"] = 40,
        }));

        (SwitchTimeline waited, ManualTime waitedTime) = Begin(connect: true, SwitchTrigger.Menu);
        waitedTime.Advance(TimeSpan.FromMilliseconds(300));
        waited.CoreBegan();
        waited.Path = SwitchPath.Direct;
        Phase(waited, waitedTime, SwitchPhase.FirstPass, 1700);
        waited.MarkActive();
        waited.Complete(OpStatus.Success, cancelled: false);
        cases.Add(new("to-pc active after waiting behind another operation, from the menu", Line(waited), new Dictionary<string, object?>
        {
            ["Kind"] = "active", ["Trigger"] = "menu", ["QueuedMs"] = 300, ["ActiveMs"] = 2000,
        }));

        // A clock that failed: every figure is "-", the line is still a line, and nothing reads as 0.
        var brokenTime = new BrokenClock();
        var broken = new SwitchTimeline(brokenTime, new CapturingLog(), connect: true, SwitchTrigger.Click);
        brokenTime.Broken = true;
        broken.MarkActive();
        broken.Complete(OpStatus.Success, cancelled: false);
        cases.Add(new("to-pc active with a clock that failed", "2026-09-22T01:31:42.000Z INFO " + SwitchTimelineText.Format(broken), new Dictionary<string, object?>
        {
            ["Kind"] = "active", ["ActiveMs"] = null, ["QueuedMs"] = null, ["TotalMs"] = null,
        }));

        // A figure too big to be milliseconds is a line that is there with no figure in it: it reads as nothing, never as
        // a number and never as 0, and the switch is not counted as measured.
        cases.Add(new("to-pc active with a figure too big to be milliseconds", RealSwitchLines.FullLine(ClickToPc().Replace("active after 1719 ms", "active after 99999999999999999999 ms", StringComparison.Ordinal)), new Dictionary<string, object?>
        {
            ["Kind"] = "active", ["Trigger"] = "click", ["ActiveMs"] = null, ["QueuedMs"] = 0,
        }));

        foreach ((SwitchBlockedAgain again, string word) in new[]
                 {
                     (SwitchBlockedAgain.Yes, "yes"), (SwitchBlockedAgain.No, "no"), (SwitchBlockedAgain.NotNeeded, "not-needed"),
                 })
        {
            (SwitchTimeline notActive, ManualTime notActiveTime) = Begin(connect: true, SwitchTrigger.ShortcutToPc);
            notActiveTime.Advance(TimeSpan.FromMilliseconds(15012));
            notActive.Path = SwitchPath.AllowFirst;
            notActive.BlockedAgain = again;
            notActive.Complete(OpStatus.Failed, cancelled: false);
            cases.Add(new("to-pc not active, blocked again " + word, Line(notActive), new Dictionary<string, object?>
            {
                ["Direction"] = "to-pc", ["Kind"] = "notactive", ["Trigger"] = "shortcut-to-pc", ["Path"] = "allow-first", ["Outcome"] = "Failed",
                ["BlockedAgain"] = word, ["TotalMs"] = 15012, ["AcceptedUtc"] = AcceptedText,
            }));
        }

        cases.Add(new("to-phone released and at rest, by click", Line(Released(SwitchTrigger.Click, 9, 282)), new Dictionary<string, object?>
        {
            ["Direction"] = "to-phone", ["Kind"] = "released", ["Trigger"] = "click", ["AtRest"] = true, ["Reason"] = null,
            ["ReleasedMs"] = 9, ["AtRestMs"] = 291, ["QueuedMs"] = 0, ["BlockMs"] = 282, ["TotalMs"] = 291, ["AcceptedUtc"] = AcceptedText,
        }));
        cases.Add(new("to-phone released and at rest, by shortcut", Line(Released(SwitchTrigger.ShortcutToPhone, 58, 291)), new Dictionary<string, object?>
        {
            ["Kind"] = "released", ["Trigger"] = "shortcut-to-phone", ["AtRest"] = true, ["ReleasedMs"] = 58, ["AtRestMs"] = 349, ["BlockMs"] = 291, ["TotalMs"] = 349,
        }));

        foreach (string reason in new[]
                 {
                     SwitchTimelineText.ReasonBlockAtBootOff, SwitchTimelineText.ReasonBlockDidNotTake,
                     SwitchTimelineText.ReasonStatusUnreadable, SwitchTimelineText.ReasonNotSetUp,
                 })
        {
            (SwitchTimeline notAtRest, ManualTime notAtRestTime) = Begin(connect: false, SwitchTrigger.Click);
            notAtRest.CoreBegan();
            notAtRestTime.Advance(TimeSpan.FromMilliseconds(9));
            notAtRest.MarkReleased();
            notAtRest.MarkNotAtRest(reason);
            notAtRest.Complete(OpStatus.Partial, cancelled: false);
            cases.Add(new("to-phone released, not at rest: " + reason, Line(notAtRest), new Dictionary<string, object?>
            {
                ["Kind"] = "released", ["Trigger"] = "click", ["AtRest"] = false, ["Reason"] = reason, ["ReleasedMs"] = 9, ["AtRestMs"] = null, ["BlockMs"] = null,
            }));
        }

        (SwitchTimeline held, ManualTime heldTime) = Begin(connect: false, SwitchTrigger.ShortcutToPhone);
        heldTime.Advance(TimeSpan.FromMilliseconds(12000));
        held.MarkAtRest();
        held.Complete(OpStatus.Failed, cancelled: false);
        cases.Add(new("to-phone not released, at rest", Line(held), new Dictionary<string, object?>
        {
            ["Direction"] = "to-phone", ["Kind"] = "notreleased", ["Trigger"] = "shortcut-to-phone", ["Outcome"] = "Failed", ["AtRest"] = true, ["TotalMs"] = 12000,
        }));

        (SwitchTimeline open, ManualTime openTime) = Begin(connect: false, SwitchTrigger.Click);
        openTime.Advance(TimeSpan.FromMilliseconds(12000));
        open.MarkNotAtRest(SwitchTimelineText.ReasonBlockDidNotTake);
        open.Complete(OpStatus.Failed, cancelled: false);
        cases.Add(new("to-phone not released, not at rest", Line(open), new Dictionary<string, object?>
        {
            ["Kind"] = "notreleased", ["Trigger"] = "click", ["AtRest"] = false, ["TotalMs"] = 12000,
        }));

        (SwitchTimeline cancelledPc, ManualTime cancelledPcTime) = Begin(connect: true, SwitchTrigger.ShortcutToggle);
        cancelledPcTime.Advance(TimeSpan.FromMilliseconds(220));
        cancelledPc.Complete(OpStatus.Failed, cancelled: true);
        cases.Add(new("to-pc cancelled", Line(cancelledPc), new Dictionary<string, object?>
        {
            ["Direction"] = "to-pc", ["Kind"] = "cancelled", ["Trigger"] = "shortcut-toggle", ["TotalMs"] = 220,
        }));

        (SwitchTimeline cancelledPhone, ManualTime cancelledPhoneTime) = Begin(connect: false, SwitchTrigger.Click);
        cancelledPhoneTime.Advance(TimeSpan.FromMilliseconds(5));
        cancelledPhone.Complete(OpStatus.Failed, cancelled: true);
        cases.Add(new("to-phone cancelled", Line(cancelledPhone), new Dictionary<string, object?>
        {
            ["Direction"] = "to-phone", ["Kind"] = "cancelled", ["Trigger"] = "click", ["TotalMs"] = 5,
        }));

        return cases;
    }

    // Shapes the script must refuse: a figure left out, a figure that is not a number, text after the line, a shape that
    // is not one of Earshot's. Each is a real line with one thing wrong, so the refusal is about that one thing.
    public static IReadOnlyList<(string Label, string Line)> Mangled()
    {
        string good = ClickToPc();
        string phone = ShortcutToPhone();
        return
        [
            ("a figure left out", "2026-09-22T01:31:42.000Z INFO " + good.Replace("active after 1719 ms", "active after  ms", StringComparison.Ordinal)),
            ("a figure that is not a number", "2026-09-22T01:31:42.000Z INFO " + good.Replace("active after 1719 ms", "active after ?? ms", StringComparison.Ordinal)),
            ("a phase figure that is not a number", "2026-09-22T01:31:42.000Z INFO " + good.Replace("allow 251", "allow two", StringComparison.Ordinal)),
            ("the counter the fake log adds to other lines", "2026-09-22T01:31:42.000Z INFO " + good + " (1)"),
            ("the accepted time left out", "2026-09-22T01:31:42.000Z INFO " + good.Replace("accepted " + AcceptedText, "accepted", StringComparison.Ordinal)),
            ("an accepted time that is not a time", "2026-09-22T01:31:42.000Z INFO " + good.Replace(AcceptedText, "2026-13-45T99:99:99.999Z", StringComparison.Ordinal)),
            ("a to-phone figure left out", "2026-09-22T01:31:42.000Z INFO " + phone.Replace("released after 58 ms", "released after  ms", StringComparison.Ordinal)),
            ("a to-phone at rest figure that is not a number", "2026-09-22T01:31:42.000Z INFO " + phone.Replace("at rest after 349 ms", "at rest after ?? ms", StringComparison.Ordinal)),
            ("an upper-case word where the shape is lower case", "2026-09-22T01:31:42.000Z INFO " + good.Replace("path allow-first", "path Allow-First", StringComparison.Ordinal)),
            ("another line about the same thing", "2026-09-22T01:31:42.000Z INFO connect: Success. Connected."),
        ];
    }

    private static (SwitchTimeline Timeline, ManualTime Time) Begin(bool connect, SwitchTrigger trigger)
    {
        var time = new ManualTime();
        return (new SwitchTimeline(time, new CapturingLog(), connect, trigger), time);
    }

    private static void Phase(SwitchTimeline t, ManualTime time, SwitchPhase phase, double milliseconds)
    {
        long mark = t.Mark();
        time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        t.Record(phase, mark);
    }

    // A switch to this PC that ran the phases given (null for one that did not run), then took the protection check.
    private static SwitchTimeline Active(SwitchTrigger trigger, SwitchPath path, double firstPass, double? status, double? allow, double? endpoints, double? connect, double protection)
    {
        (SwitchTimeline t, ManualTime time) = Begin(connect: true, trigger);
        t.CoreBegan();
        t.Path = path;
        Phase(t, time, SwitchPhase.FirstPass, firstPass);
        if (status is { } s)
        {
            Phase(t, time, SwitchPhase.Status, s);
        }

        if (allow is { } a)
        {
            Phase(t, time, SwitchPhase.Allow, a);
        }

        if (endpoints is { } e)
        {
            Phase(t, time, SwitchPhase.Endpoints, e);
        }

        if (connect is { } c)
        {
            Phase(t, time, SwitchPhase.Connect, c);
        }

        t.MarkActive();
        Phase(t, time, SwitchPhase.Protection, protection);
        t.Complete(OpStatus.Success, cancelled: false);
        return t;
    }

    // A switch to the phone that let go after releasedAfter ms and took block ms to block, at rest when the block ended.
    private static SwitchTimeline Released(SwitchTrigger trigger, double releasedAfter, double block)
    {
        (SwitchTimeline t, ManualTime time) = Begin(connect: false, trigger);
        t.CoreBegan();
        time.Advance(TimeSpan.FromMilliseconds(releasedAfter));
        t.MarkReleased();
        Phase(t, time, SwitchPhase.Block, block);
        t.MarkAtRest();
        t.Complete(OpStatus.Success, cancelled: false);
        return t;
    }

    private static string Line(SwitchTimeline t) => SwitchTimelineText.Format(t);

    // Full log lines, in the real single-space FileLog.AppendEntry shape.
    internal static string FullLine(string message) => "2026-09-22T01:31:42.001Z INFO " + message;

    private sealed class BrokenClock : TimeProvider
    {
        private readonly ManualTime _inner = new();

        public bool Broken { get; set; }

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override long GetTimestamp() => Broken ? throw new InvalidOperationException("clock") : _inner.GetTimestamp();
    }
}

[TestClass]
public sealed class SwitchFormatterAgainstRealParserTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public void TheScriptsOwnFunctionsReadEveryLineTheRealFormatterWritesAndRefuseEveryMangledOne()
    {
        string host = WindowsPowerShellHost.Path51();
        if (!File.Exists(host))
        {
            Assert.Inconclusive(
                "Windows PowerShell 5.1 is not installed at " + host + ", so the real formatter's lines were never " +
                "run through the script's own parser and this settles nothing about it.");
        }

        string root = RepositoryRoot();
        string script = Path.Combine(root, "tools", "live-tests", "selftest", "Test-RealSwitchLines.ps1");
        Assert.IsTrue(File.Exists(script), "Test-RealSwitchLines.ps1 was not found at " + script + ".");

        string casesFile = Path.Combine(Path.GetTempPath(), "earshot-switch-cases-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(casesFile, CasesJson());

            (int exit, string output, string errors) = RunScript(host, script, casesFile, root);

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(output),
                "Test-RealSwitchLines.ps1 printed nothing. Host " + host + ", exit " +
                exit.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + errors);

            JsonElement result = ReadLastJsonObject(output);
            var problems = new List<string>();
            if (result.TryGetProperty("problems", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement problem in listed.EnumerateArray())
                {
                    problems.Add(problem.ToString());
                }
            }

            Assert.IsEmpty(
                problems,
                "The script's own functions did not read the real lines and settings as they are:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems) + Environment.NewLine + output);

            Assert.IsTrue(
                result.GetProperty("ok").GetBoolean(),
                "Test-RealSwitchLines.ps1 reported ok: false with no problem listed, which should not happen." + Environment.NewLine + output);

            Assert.IsGreaterThan(200, result.GetProperty("checked").GetInt32(), "Far fewer checks ran than the cases call for, which cannot be right.");
            Assert.AreEqual(0, exit, "Test-RealSwitchLines.ps1 exited " + exit.ToString(CultureInfo.InvariantCulture) + " with nothing to report.");
        }
        finally
        {
            File.Delete(casesFile);
        }
    }

    // The literal text in Fakes.psm1, against the formatter. A fixture that drifted would let every self-test case pass
    // on lines the application never writes.
    [TestMethod]
    public void SwitchFixturesInFakesPsm1MatchTheRealFormatter()
    {
        string path = Path.Combine(RepositoryRoot(), "tools", "live-tests", "selftest", "Fakes.psm1");
        Assert.IsTrue(File.Exists(path), path + " is gone, so the switch fixtures cannot be checked.");
        string text = File.ReadAllText(path);

        var problems = new List<string>();
        foreach ((string label, string line) in new[]
                 {
                     ("to this PC by click", RealSwitchLines.ClickToPc()),
                     ("to this PC by shortcut", RealSwitchLines.ShortcutToPc()),
                     ("to the phone by click", RealSwitchLines.ClickToPhone()),
                     ("to the phone by shortcut", RealSwitchLines.ShortcutToPhone()),
                     ("a switch to this PC that timed out", RealSwitchLines.TimedOut()),
                 })
        {
            if (!text.Contains(line, StringComparison.Ordinal))
            {
                problems.Add("Fakes.psm1 has no line matching the real formatter's " + label + " shape: \"" + line + "\"");
            }
        }

        // The two shortcut press lines and the protect-on line are Earshot's own text too.
        foreach (string message in new[] { "Hotkey: switch to this PC.", "Hotkey: switch to the phone.", "protect-on (Allow): Success. Audio quality protected" })
        {
            if (!text.Contains(message, StringComparison.Ordinal))
            {
                problems.Add("Fakes.psm1 has no fixture line \"" + message + "\"");
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    // The words the script looks for in the log are the words Earshot writes when a shortcut is pressed.
    [TestMethod]
    public void TheScriptLooksForTheShortcutPressLinesTheTrayWritesAndReadsTheDefaultChordsFromSettings()
    {
        string root = RepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "tools", "live-tests", "16-FastSwitch.ps1"));
        string tray = File.ReadAllText(Path.Combine(root, "src", "Earshot", "App", "TrayContext.Switch.cs"));

        StringAssert.Contains(script, "'Hotkey: switch to'");
        StringAssert.Contains(tray, "\"Hotkey: switch to \"");
        StringAssert.Contains(script, "$script:DefaultToPc = '" + HotkeySettings.DefaultSwitchToPc + "'");
        StringAssert.Contains(script, "$script:DefaultToPhone = '" + HotkeySettings.DefaultSwitchToPhone + "'");
        StringAssert.Contains(script, "'" + GateVerbs.ProtectOn + " ('");
    }

    private static string CasesJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();

            json.WriteStartArray("lines");
            foreach (RealSwitchLines.LineCase c in RealSwitchLines.Lines())
            {
                json.WriteStartObject();
                json.WriteString("label", c.Label);
                json.WriteString("line", c.Line.Contains(" INFO ", StringComparison.Ordinal) ? c.Line : RealSwitchLines.FullLine(c.Line));
                json.WriteStartObject("expected");
                foreach ((string name, object? value) in c.Expected)
                {
                    WriteValue(json, name, value);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("mangled");
            foreach ((string label, string line) in RealSwitchLines.Mangled())
            {
                json.WriteStartObject();
                json.WriteString("label", label);
                json.WriteString("line", line);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("owner");
            foreach ((string label, string? accepted, int? released, string heard, string legStart, int? expected) in OwnerCases)
            {
                json.WriteStartObject();
                json.WriteString("label", label);
                WriteValue(json, "accepted", accepted);
                WriteValue(json, "releasedMs", released);
                json.WriteString("heard", heard);
                json.WriteString("legStart", legStart);
                WriteValue(json, "expected", expected);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("median");
            foreach ((string label, int[] values, int? expected) in MedianCases)
            {
                json.WriteStartObject();
                json.WriteString("label", label);
                json.WriteStartArray("values");
                foreach (int value in values)
                {
                    json.WriteNumberValue(value);
                }

                json.WriteEndArray();
                WriteValue(json, "expected", expected);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("settings");
            foreach ((string label, string content) in SettingsCases())
            {
                HotkeySettings read = JsonSerializer.Deserialize(content, SettingsJsonContext.Default.EarshotSettings)!.Hotkeys;
                json.WriteStartObject();
                json.WriteString("label", label);
                json.WriteString("json", content);
                json.WriteBoolean("enabled", read.Enabled);
                json.WriteString("toPc", read.SwitchToPc);
                json.WriteString("toPhone", read.SwitchToPhone);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly (string Label, string? Accepted, int? Released, string Heard, string LegStart, int? Expected)[] OwnerCases =
    [
        ("the moment they heard, less the release Earshot measured", "2026-09-29T14:00:00.000Z", 58, "2026-09-29T14:00:01.000Z", "2026-09-29T13:59:50.000Z", 942),
        ("no release measured", "2026-09-29T14:00:00.000Z", null, "2026-09-29T14:00:01.000Z", "2026-09-29T13:59:50.000Z", null),
        ("a line accepted before this leg began is another leg's", "2026-09-29T13:59:00.000Z", 58, "2026-09-29T14:00:01.000Z", "2026-09-29T13:59:50.000Z", null),
        ("heard before the release ended is not a time", "2026-09-29T14:00:00.000Z", 5000, "2026-09-29T14:00:01.000Z", "2026-09-29T13:59:50.000Z", null),
        ("no accepted time", null, 58, "2026-09-29T14:00:01.000Z", "2026-09-29T13:59:50.000Z", null),
    ];

    private static readonly (string Label, int[] Values, int? Expected)[] MedianCases =
    [
        ("an odd number", [1719, 2749, 2749], 2749),
        ("an even number, the mean rounded down", [1719, 2750], 2234),
        ("one figure", [5], 5),
        ("four figures", [4, 1, 3, 2], 2),
        ("none", [], null),
    ];

    // Settings files Earshot may hold, from an older build's to a cleared one, and what its own store reads from each.
    private static IEnumerable<(string Label, string Content)> SettingsCases()
    {
        yield return ("no hotkeys member", "{ \"SchemaVersion\": 1 }");
        yield return ("an older file, nothing typed, shortcuts off", "{ \"Hotkeys\": { \"Enabled\": false, \"ToggleConnection\": \"\", \"ToggleAudioProtection\": \"\", \"ToggleBlockAtBoot\": \"\", \"SpeakStatus\": \"\" } }");
        yield return ("an older file with a chord typed and shortcuts on", "{ \"Hotkeys\": { \"Enabled\": true, \"ToggleConnection\": \"Ctrl+Alt+C\" } }");
        yield return ("an older file with a chord typed and shortcuts off", "{ \"Hotkeys\": { \"Enabled\": false, \"ToggleConnection\": \"Ctrl+Alt+C\" } }");
        yield return ("both members present, cleared, shortcuts off", "{ \"Hotkeys\": { \"Enabled\": false, \"SwitchToPc\": \"\", \"SwitchToPhone\": \"\" } }");
        yield return ("only the to-PC member present and cleared, shortcuts off", "{ \"Hotkeys\": { \"Enabled\": false, \"SwitchToPc\": \"\" } }");
        yield return ("a chord typed the same as a default", "{ \"Hotkeys\": { \"Enabled\": true, \"ToggleConnection\": \"Ctrl+Alt+Shift+A\" } }");
        yield return ("only enabled", "{ \"Hotkeys\": { \"Enabled\": true } }");
        yield return ("both changed", "{ \"Hotkeys\": { \"Enabled\": true, \"SwitchToPc\": \"Ctrl+Alt+P\", \"SwitchToPhone\": \"Ctrl+Alt+O\" } }");
        yield return ("what the shipped serialiser writes", JsonSerializer.Serialize(new EarshotSettings(), SettingsJsonContext.Default.EarshotSettings));
    }

    private static void WriteValue(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(name);
                break;
            case bool b:
                json.WriteBoolean(name, b);
                break;
            case int i:
                json.WriteNumber(name, i);
                break;
            case string s:
                json.WriteString(name, s);
                break;
            default:
                throw new InvalidOperationException("A value of type " + value.GetType().Name + " is not written.");
        }
    }

    private static JsonElement ReadLastJsonObject(string output)
    {
        int start = output.LastIndexOf("\n{", StringComparison.Ordinal);
        string json = start < 0 ? output.Trim() : output[(start + 1)..].Trim();
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new AssertFailedException(
                "Test-RealSwitchLines.ps1 did not print a JSON result: " + error.Message + Environment.NewLine + output);
        }
    }

    private static (int Exit, string Output, string Errors) RunScript(string host, string script, string casesFile, string root)
    {
        ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(host, new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-Root", root, "-CasesFile", casesFile,
        });

        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(errors, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Test-RealSwitchLines.ps1 did not finish within " + RunTimeout + ".");
        }

        process.WaitForExit();
        lock (output)
        {
            lock (errors)
            {
                return (process.ExitCode, output.ToString(), errors.ToString());
            }
        }
    }

    private static void Append(StringBuilder text, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (text)
        {
            text.AppendLine(line);
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

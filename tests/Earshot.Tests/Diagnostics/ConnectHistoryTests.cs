using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Tests.Phase4;
using Earshot.Tests.Phase6;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Diagnostics;

[TestClass]
public sealed class ConnectHistoryTests
{
    private static ConnectHistoryEntry Entry(DateTimeOffset at, string outcome = ConnectHistoryOutcomes.Connected, IReadOnlyList<HistoryStep>? steps = null) =>
        new(at, ConnectHistoryActions.Connect, "present-enabled", "direct", steps ?? [], false, outcome, "None", 1200);

    [TestMethod]
    public void AnEntryIsWrittenAndReadBack()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        var history = new ConnectHistory(Path.Combine(temp.Path, "connect-history.jsonl"), time, new CapturingLog());

        StepOutcome written = history.Append(Entry(time.GetUtcNow(), steps: [new HistoryStep("ks-reconnect", 0, "S_OK")]));

        Assert.IsTrue(written.Ok, written.Detail);
        ConnectHistoryEntry read = history.Read().Single();
        Assert.AreEqual("connect", read.Action);
        Assert.AreEqual("present-enabled", read.StartState);
        Assert.AreEqual(1200, read.DurationMs);
        Assert.AreEqual("ks-reconnect", read.Steps.Single().Name);
    }

    // An append lock that cannot be opened (another account's, which the tray may not open) never throws into the switch: the
    // code is logged and the entry is written without the lock.
    [TestMethod]
    [DataRow("denied")]
    [DataRow("cannot-open")]
    public void ALockThatCannotBeOpenedIsLoggedAndTheEntryIsStillWritten(string kind)
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        var log = new CapturingLog();
        Exception thrown = kind == "denied" ? new UnauthorizedAccessException("denied") : new WaitHandleCannotBeOpenedException("cannot open");
        var history = new ConnectHistory(Path.Combine(temp.Path, "connect-history.jsonl"), time, log, _ => throw thrown);

        StepOutcome written = history.Append(Entry(time.GetUtcNow()));

        Assert.IsTrue(written.Ok, written.Detail);
        Assert.HasCount(1, history.Read());
        Assert.IsTrue(log.Entries.Any(e => e.Message.Contains("HRESULT 0x" + thrown.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            string.Join(" | ", log.Entries.Select(e => e.Message)));
    }

    // The tray and a fix run append to the same file. Each append reads, adds and writes it all, so two at once must not lose
    // each other's entry or trip over one temp file.
    [TestMethod]
    public void TwoWritersAppendingAtOnceLoseNothing()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        string file = Path.Combine(temp.Path, "connect-history.jsonl");
        var log = new CapturingLog();
        var tray = new ConnectHistory(file, time, log);
        var fix = new ConnectHistory(file, time, log);

        Parallel.For(0, 80, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            Assert.IsTrue((i % 2 == 0 ? tray : fix).Append(Entry(time.GetUtcNow())).Ok));

        Assert.HasCount(80, tray.Read());
        Assert.IsFalse(log.Entries.Any(e => e.Level == LogLevel.Warn), string.Join(" | ", log.Entries.Select(e => e.Message)));
    }

    [TestMethod]
    public void EntriesOlderThanFourteenDaysAreDroppedWhenALineIsWritten()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        var history = new ConnectHistory(Path.Combine(temp.Path, "h.jsonl"), time, new CapturingLog());
        history.Append(Entry(time.GetUtcNow(), ConnectHistoryOutcomes.Failed));
        time.Advance(TimeSpan.FromDays(13));
        history.Append(Entry(time.GetUtcNow(), ConnectHistoryOutcomes.Blocked));
        Assert.HasCount(2, history.Read());

        time.Advance(TimeSpan.FromDays(2));
        history.Append(Entry(time.GetUtcNow()));

        IReadOnlyList<ConnectHistoryEntry> kept = history.Read();
        Assert.HasCount(2, kept, "The first line is 15 days old and went; the 2-day-old one stays.");
        Assert.IsFalse(kept.Any(e => e.Outcome == ConnectHistoryOutcomes.Failed));
        Assert.HasCount(2, File.ReadAllLines(history.FilePath), "The file itself was rewritten, not only filtered on read.");
    }

    [TestMethod]
    public void ALineBuiltFromStepsCarryingIdsContainsNoneOfThem()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        var history = new ConnectHistory(Path.Combine(temp.Path, "h.jsonl"), time, new CapturingLog());
        StepOutcome[] steps =
        [
            StepOutcomes.FromConfigRet("cm-enable:" + RecordedNodes.AirPodsDeviceNode, 0),
            StepOutcomes.FromConfigRet("cm-enable:" + RecordedNodes.AirPodsTargets[8], 0),
            StepOutcomes.FromHResult("ks-reconnect:{0.0.0.00000000}.{55555555-6666-4777-8888-999999999901}", 0, "a2dp: Jonathan’s AirPods Pro"),
            StepOutcomes.FromConfigRet("cfg-set:" + RecordedNodes.AirPodsTargets[0], 0, "BTHENUM " + RecordedNodes.AirPodsAddress),
        ];

        history.Append(Entry(time.GetUtcNow(), steps: ConnectHistory.FromSteps(steps)));

        string text = File.ReadAllText(history.FilePath) + string.Join("\n", history.ReadForDiagnostics());
        foreach (string secret in new[] { RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer.ToString("D"), "55555555", "Jonathan", "BTHENUM", "b&1a2b3c4d", "0000110B", "AirPods Pro" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("cm-enable:device-node", text);
        Assert.Contains("cm-enable:service-node", text);
        Assert.Contains("ks-reconnect", text);
        Assert.Contains("cfg-set:service-node", text);
    }

    [TestMethod]
    public void StepKindKeepsTheKindAndDropsTheName()
    {
        Assert.AreEqual("cm-enable:device-node", ConnectHistory.StepKind(@"cm-enable:BTHENUM\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C"));
        Assert.AreEqual("cm-disable:service-node", ConnectHistory.StepKind(@"cm-disable:BTHENUM\{x}_VID&0001004C_PID&2027\b&1"));
        Assert.AreEqual("ks-reconnect:endpoint", ConnectHistory.StepKind(@"ks-reconnect:{0.0.0.00000000}.{a}"));
        Assert.AreEqual("ks-reconnect", ConnectHistory.StepKind("ks-reconnect:src"));
        Assert.AreEqual("task-run", ConnectHistory.StepKind(@"task-run:\Earshot\Gate"));
        Assert.AreEqual("allow-connect", ConnectHistory.StepKind("allow-connect"));
    }

    [TestMethod]
    public void AFileThatCannotBeWrittenIsAFailedStepWithItsCodeAndNeverThrows()
    {
        using var temp = new TempFolder();
        string blocker = Path.Combine(temp.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var log = new CapturingLog();
        var history = new ConnectHistory(Path.Combine(blocker, "h.jsonl"), new ManualTime(), log);

        StepOutcome written = history.Append(Entry(DateTimeOffset.UtcNow));

        Assert.IsFalse(written.Ok);
        Assert.AreNotEqual(0, written.Code);
        Assert.IsTrue(log.Has(LogLevel.Warn, "could not be written"));
    }

    [TestMethod]
    public void ALineThatIsNotValidIsSkippedAndLogged()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        var log = new CapturingLog();
        string file = Path.Combine(temp.Path, "h.jsonl");
        File.WriteAllText(file, "not json\n");
        var history = new ConnectHistory(file, time, log);

        history.Append(Entry(time.GetUtcNow()));

        Assert.HasCount(1, history.Read());
        Assert.HasCount(1, File.ReadAllLines(file), "The bad line is dropped with the old ones.");
    }

    [TestMethod]
    public void DiagnosticsLinesNameTheActionTheOutcomeAndTheCodes()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        var history = new ConnectHistory(Path.Combine(temp.Path, "h.jsonl"), time, new CapturingLog());
        history.Append(new ConnectHistoryEntry(time.GetUtcNow(), ConnectHistoryActions.Connect, "away-flagged", "allow-first",
            [new HistoryStep("cm-enable:service-node", 0, "CR_SUCCESS")], Retry: true, ConnectHistoryOutcomes.Failed, "PcSide", 20001));

        string line = history.ReadForDiagnostics().Single();

        Assert.Contains("connect start=away-flagged path=allow-first retried outcome=failed class=PcSide 20001 ms", line);
        Assert.Contains("cm-enable:service-node CR_SUCCESS", line);
    }
}

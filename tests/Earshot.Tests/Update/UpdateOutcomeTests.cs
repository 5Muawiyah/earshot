using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// After the hand-over the tray has exited, so a refused or failed elevated update used to be recorded only in the
// machine log and the owner was never told. The outcome is now written to update-outcome.json in the machine folder
// and the tray says it once at its next start. Everything here runs against temporary folders with fakes; nothing
// elevates and nothing touches Program Files or ProgramData.
[TestClass]
public sealed class UpdateOutcomeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string IdA = "0123456789abcdef0123456789abcdef";
    private const string IdB = "fedcba9876543210fedcba9876543210";

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static InstallResult Refusal(string step, string codeName, int code, GateExitCode outcome = GateExitCode.Failed) =>
        new(outcome, [new StepOutcome("update-running-from", true, 0, "S_OK", "C:\\Program Files\\Earshot"), new StepOutcome(step, false, code, codeName, "detail with C:\\Users\\someone\\path")]);

    private static InstallResult Done(GateExitCode outcome = GateExitCode.Success) =>
        new(outcome, [new StepOutcome("update-start-install", outcome == GateExitCode.Success, 0, "S_OK", "started")]);

    private sealed class Machine : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Machine()
        {
            Folder = _temp.File("Earshot");
            Directory.CreateDirectory(Folder);
            Folders = new FakeFolderSecurity { SddlFor = _ => Sddl.MachineFolder };
        }

        public string Folder { get; }

        public FakeFolderSecurity Folders { get; }

        public CapturingLog Log { get; } = new();

        public FixedTime Time { get; } = new(Now);

        public GateStore Store => new(Folder);

        public string Elsewhere(string name) => _temp.File(name);

        public UpdateOutcomeRecorder Recorder() => new(Folder, Folders, Log, Time);

        public void Dispose() => _temp.Dispose();
    }

    // ---- what each run records ----

    [TestMethod]
    public void AnUpdateRunThatStartedTheInstallRecordsInstalling()
    {
        UpdateOutcome outcome = UpdateOutcomes.ForUpdateRun(Done(), IdA, Now);

        Assert.AreEqual(UpdateOutcomeKind.Installing, outcome.Kind);
        Assert.AreEqual("", outcome.Reason);
        Assert.AreEqual("", outcome.Code);
    }

    [TestMethod]
    public void AnUpdateRunThatStoppedFirstRecordsWhyAndTheRawCodeAndNoPath()
    {
        InstallResult stopped = Refusal("wait-tray", "NOT_ATTEMPTED", -1);

        UpdateOutcome outcome = UpdateOutcomes.ForUpdateRun(stopped, IdA, Now);

        Assert.AreEqual(UpdateOutcomeKind.Refused, outcome.Kind);
        Assert.AreEqual("Earshot did not close in time", outcome.Reason);
        Assert.AreEqual("NOT_ATTEMPTED", outcome.Code);
        Assert.DoesNotContain("Users", outcome.Reason, "A step's own detail holds paths and never reaches the owner.");
    }

    [TestMethod]
    public void AnUnnamedFailureStillSaysSomethingAndCarriesTheResultName()
    {
        var stopped = new InstallResult(GateExitCode.FolderNotSecure, []);

        UpdateOutcome outcome = UpdateOutcomes.ForUpdateRun(stopped, IdA, Now);

        Assert.AreEqual(UpdateOutcomeKind.Refused, outcome.Kind);
        Assert.AreEqual("a folder's permissions were not as Earshot needs", outcome.Reason);
        Assert.AreEqual("folder-not-secure", outcome.Code);
    }

    [TestMethod]
    public void AnInstallThatFinishesCompletesTheRecordTheUpdateRunLeft()
    {
        UpdateOutcome installing = UpdateOutcomes.ForUpdateRun(Done(), IdA, Now);

        UpdateOutcome? completed = UpdateOutcomes.ForInstallRun(installing, Done(), "1.2.0", IdB, Now.AddSeconds(30));

        Assert.IsNotNull(completed);
        Assert.AreEqual(UpdateOutcomeKind.Installed, completed.Kind);
        Assert.AreEqual("1.2.0", completed.Version);
        Assert.AreEqual(IdB, completed.Id, "A new Id, so the tray shows it even after showing the earlier one.");
    }

    [TestMethod]
    public void AnInstallThatDoesNotFinishRecordsFailedNotInstalled()
    {
        UpdateOutcome installing = UpdateOutcomes.ForUpdateRun(Done(), IdA, Now);

        UpdateOutcome? completed = UpdateOutcomes.ForInstallRun(installing, new InstallResult(GateExitCode.Partial, [new StepOutcome("service-start", false, 5, "E_ACCESSDENIED", "d")]), "1.2.0", IdB, Now.AddSeconds(30));

        Assert.IsNotNull(completed);
        Assert.AreEqual(UpdateOutcomeKind.Failed, completed.Kind);
        Assert.AreEqual("setup finished only in part", completed.Reason);
        Assert.AreEqual("E_ACCESSDENIED", completed.Code);
    }

    [TestMethod]
    public void AnInstallThatIsNotAnUpdateLeavesTheRecordAlone()
    {
        UpdateOutcome installed = new(IdA, Now, UpdateOutcomeKind.Installed, "1.1.0", "", "");
        UpdateOutcome refused = new(IdA, Now, UpdateOutcomeKind.Refused, "", "x", "y");
        UpdateOutcome stale = new(IdA, Now, UpdateOutcomeKind.Installing, "", "", "");

        Assert.IsNull(UpdateOutcomes.ForInstallRun(null, Done(), "1.2.0", IdB, Now), "A first setup has no record.");
        Assert.IsNull(UpdateOutcomes.ForInstallRun(installed, Done(), "1.2.0", IdB, Now.AddSeconds(5)), "A repair after a finished update.");
        Assert.IsNull(UpdateOutcomes.ForInstallRun(refused, Done(), "1.2.0", IdB, Now.AddSeconds(5)));
        Assert.IsNull(UpdateOutcomes.ForInstallRun(stale, Done(), "1.2.0", IdB, Now + UpdateOutcomes.InstallWindow + TimeSpan.FromSeconds(1)), "A setup run by hand long afterwards.");
    }

    // ---- what the owner is told ----

    [TestMethod]
    public void EachOutcomeHasPlainWordsAndAnInstallStillInItsWindowHasNone()
    {
        Assert.AreEqual("Earshot was updated to 1.2.0.", UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Installed, "1.2.0", "", ""), Now));
        Assert.AreEqual(
            "The update did not finish: Earshot did not close in time. Nothing was changed.",
            UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Refused, "", "Earshot did not close in time", "NOT_ATTEMPTED"), Now));
        StringAssert.StartsWith(UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Failed, "", "setup finished only in part", "x"), Now), "The update did not finish: setup finished only in part.");
        Assert.IsNull(UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Installing, "", "", ""), Now.AddMinutes(1)));
        StringAssert.StartsWith(UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Installing, "", "", ""), Now.AddHours(1)), "The update may not have finished");
    }

    // An update that was a repair (the release of the installed version, through the update path) is said in the repair's
    // words, for every way it can end.
    [TestMethod]
    public void AnUpdateThatWasARepairByDownloadIsSaidInTheRepairsWords()
    {
        Assert.AreEqual("Earshot was repaired.", UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Installed, "1.2.0", "", ""), Now, repairByDownload: true));
        Assert.AreEqual(
            "The repair did not finish: Earshot did not close in time. Nothing was changed.",
            UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Refused, "", "Earshot did not close in time", "c"), Now, repairByDownload: true));
        Assert.AreEqual(
            "The repair did not finish: setup finished only in part. Choose Repair Earshot from its menu to repair it.",
            UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Failed, "", "setup finished only in part", "c"), Now, repairByDownload: true));
        StringAssert.StartsWith(
            UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Installing, "", "", ""), Now.AddHours(1), repairByDownload: true), "The repair may not have finished");
        Assert.AreEqual("Earshot was repaired.", UpdateOutcomes.NoticeFor(new(IdA, Now, UpdateOutcomeKind.Repaired, "1.2.0", "", ""), Now), "A repair run from the installed copy says it with no note.");
    }

    [TestMethod]
    public void ARepairOutcomeRoundTripsThroughTheStoreAndNoRepairNoticeUsesADash()
    {
        using var machine = new Machine();
        foreach (UpdateOutcomeKind kind in new[] { UpdateOutcomeKind.Repaired, UpdateOutcomeKind.RepairFailed })
        {
            var outcome = new UpdateOutcome(IdA, Now, kind, kind == UpdateOutcomeKind.Repaired ? "1.2.0" : "", kind == UpdateOutcomeKind.Repaired ? "" : "an installed file is missing or is not what was published", "NOT_ATTEMPTED");
            Assert.IsTrue(machine.Store.WriteUpdateOutcome(outcome).Ok);
            GateRead<UpdateOutcome> read = machine.Store.ReadUpdateOutcome();
            Assert.IsTrue(read.IsOk, read.Step.Detail);
            Assert.AreEqual(outcome, read.Value);
            string notice = UpdateOutcomes.NoticeFor(outcome, Now)!;
            Assert.DoesNotContain("\u2014", notice);
            Assert.DoesNotContain("HRESULT", notice);
        }
    }

    [TestMethod]
    public void NoNoticeUsesADashOrAWordTheOwnerWouldNotKnow()
    {
        var kinds = new[]
        {
            new UpdateOutcome(IdA, Now, UpdateOutcomeKind.Installed, "1.2.0", "", ""),
            new UpdateOutcome(IdA, Now, UpdateOutcomeKind.Refused, "", "Earshot did not close in time", "c"),
            new UpdateOutcome(IdA, Now, UpdateOutcomeKind.Failed, "", "setup finished only in part", "c"),
            new UpdateOutcome(IdA, Now, UpdateOutcomeKind.Installing, "", "", ""),
        };

        foreach (UpdateOutcome outcome in kinds)
        {
            string? notice = UpdateOutcomes.NoticeFor(outcome, Now.AddHours(1));
            Assert.IsNotNull(notice);
            Assert.DoesNotContain("\u2014", notice);
            Assert.DoesNotContain("HRESULT", notice);
        }
    }

    // ---- the file ----

    [TestMethod]
    public void TheFileRoundTripsThroughTheStore()
    {
        using var machine = new Machine();
        var outcome = new UpdateOutcome(IdA, Now, UpdateOutcomeKind.Refused, "", "Earshot did not close in time", "NOT_ATTEMPTED");

        Assert.IsTrue(machine.Store.WriteUpdateOutcome(outcome).Ok);
        GateRead<UpdateOutcome> read = machine.Store.ReadUpdateOutcome();

        Assert.IsTrue(read.IsOk, read.Step.Detail);
        Assert.AreEqual(outcome, read.Value);
    }

    [TestMethod]
    public void AMissingFileIsMissingAndNothingElseIsRead()
    {
        using var machine = new Machine();

        Assert.AreEqual(GateReadStatus.Missing, machine.Store.ReadUpdateOutcome().Status);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"SchemaVersion\":1,\"Id\":\"0123456789abcdef0123456789abcdef\",\"WrittenUtc\":\"2026-09-29T12:00:00.0000000Z\",\"Kind\":\"Installed\",\"Version\":\"1.2.0\",\"Reason\":\"\"}")]
    [DataRow("{\"SchemaVersion\":1,\"Id\":\"0123456789abcdef0123456789abcdef\",\"WrittenUtc\":\"2026-09-29T12:00:00.0000000Z\",\"Kind\":\"Installed\",\"Version\":\"1.2.0\",\"Reason\":\"\",\"Code\":\"\",\"Extra\":1}")]
    [DataRow("{\"SchemaVersion\":1,\"Id\":\"0123456789abcdef0123456789abcdef\",\"WrittenUtc\":\"2026-09-29T12:00:00.0000000Z\",\"Kind\":\"Bogus\",\"Version\":\"\",\"Reason\":\"\",\"Code\":\"\"}")]
    [DataRow("{\"SchemaVersion\":1,\"Id\":\"NOT-AN-ID\",\"WrittenUtc\":\"2026-09-29T12:00:00.0000000Z\",\"Kind\":\"Installed\",\"Version\":\"\",\"Reason\":\"\",\"Code\":\"\"}")]
    [DataRow("{\"SchemaVersion\":2,\"Id\":\"0123456789abcdef0123456789abcdef\",\"WrittenUtc\":\"2026-09-29T12:00:00.0000000Z\",\"Kind\":\"Installed\",\"Version\":\"\",\"Reason\":\"\",\"Code\":\"\"}")]
    [DataRow("{\"SchemaVersion\":1,\"Id\":\"0123456789abcdef0123456789abcdef\",\"WrittenUtc\":\"yesterday\",\"Kind\":\"Installed\",\"Version\":\"\",\"Reason\":\"\",\"Code\":\"\"}")]
    [DataRow("{\"SchemaVersion\":1,\"Id\":\"0123456789abcdef0123456789abcdef\",\"WrittenUtc\":\"2026-09-29T12:00:00.0000000Z\",\"Kind\":\"Refused\",\"Version\":\"\",\"Reason\":\"line\\nbreak\",\"Code\":\"\"}")]
    public void AFileThatIsNotExactlyTheShapeIsInvalidAndNeverShown(string content)
    {
        using var machine = new Machine();
        File.WriteAllText(machine.Store.UpdateOutcomeFile, content);

        GateRead<UpdateOutcome> read = machine.Store.ReadUpdateOutcome();

        Assert.AreEqual(GateReadStatus.Invalid, read.Status, read.Step.Detail);
        Assert.IsNull(read.Value);
    }

    // ---- the recorder, which writes only into a hardened machine folder ----

    [TestMethod]
    public void TheUpdateRunAndTheInstallItStartsLeaveInstallingThenInstalled()
    {
        using var machine = new Machine();
        UpdateOutcomeRecorder recorder = machine.Recorder();

        recorder.RecordUpdateRun(Done());
        Assert.AreEqual(UpdateOutcomeKind.Installing, machine.Store.ReadUpdateOutcome().Value!.Kind);

        machine.Time.Now = Now.AddSeconds(20);
        recorder.RecordInstallRun(Done(), "1.2.0");

        UpdateOutcome after = machine.Store.ReadUpdateOutcome().Value!;
        Assert.AreEqual(UpdateOutcomeKind.Installed, after.Kind);
        Assert.AreEqual("1.2.0", after.Version);
    }

    [TestMethod]
    public void ARefusedUpdateIsRecordedWithItsReasonAndCode()
    {
        using var machine = new Machine();

        machine.Recorder().RecordUpdateRun(Refusal("update-verify-zip", "NOT_ATTEMPTED", -1));

        UpdateOutcome outcome = machine.Store.ReadUpdateOutcome().Value!;
        Assert.AreEqual(UpdateOutcomeKind.Refused, outcome.Kind);
        Assert.AreEqual("the downloaded update did not match what was checked", outcome.Reason);
        Assert.AreEqual("NOT_ATTEMPTED", outcome.Code);
    }

    [TestMethod]
    public void AnInstallWithNoUpdateBehindItWritesNothing()
    {
        using var machine = new Machine();

        machine.Recorder().RecordInstallRun(Done(), "1.2.0");

        Assert.IsFalse(File.Exists(machine.Store.UpdateOutcomeFile));
    }

    [TestMethod]
    public void NothingIsWrittenIntoAFolderThatDoesNotPassItsCheck()
    {
        using var machine = new Machine();
        machine.Folders.SddlFor = _ => "O:BAG:SYD:(A;OICI;FA;;;WD)";

        machine.Recorder().RecordUpdateRun(Done());

        Assert.IsFalse(File.Exists(machine.Store.UpdateOutcomeFile));
        Assert.IsTrue(machine.Log.Has(LogLevel.Warn, "did not pass its check"));
    }

    [TestMethod]
    public void AMissingMachineFolderIsNotCreatedAndTheFailureIsLoggedWithItsCode()
    {
        using var machine = new Machine();
        string missing = machine.Elsewhere("never-made");
        var recorder = new UpdateOutcomeRecorder(missing, machine.Folders, machine.Log, machine.Time);

        recorder.RecordUpdateRun(Done());

        Assert.IsFalse(Directory.Exists(missing), "The machine folder is never created by a record of an outcome.");
        Assert.IsTrue(machine.Log.Entries.Any(e => e.Level == LogLevel.Warn && e.Message.StartsWith("update outcome:", StringComparison.Ordinal)), "Said in the log, not silent.");
    }

    // ---- the tray says it once ----

    private static UpdateOutcomeSource Source(Machine machine) =>
        new(machine.Folder, machine.Elsewhere("shown.txt"));

    [TestMethod]
    public void TheTrayShowsARefusedUpdateOnceAtItsNextStart()
    {
        using var machine = new Machine();
        Assert.IsTrue(machine.Store.WriteUpdateOutcome(new(IdA, Now, UpdateOutcomeKind.Refused, "", "Earshot did not close in time", "NOT_ATTEMPTED")).Ok);
        UpdateOutcomeSource source = Source(machine);

        StaThread.Run(() =>
        {
            using var first = new UpdateTrayHarness(outcomeSource: source);
            first.Settle();
            Assert.AreEqual(
                "The update did not finish: Earshot did not close in time. Nothing was changed.",
                first.Cards.Shown.Single().Content.Status);
            Assert.IsTrue(first.Log.Has(LogLevel.Info, "ended as Refused (NOT_ATTEMPTED)"));
        });

        StaThread.Run(() =>
        {
            using var second = new UpdateTrayHarness(outcomeSource: source);
            second.Settle();
            Assert.IsEmpty(second.Cards.Shown, "The same outcome is not shown again.");
        });
    }

    [TestMethod]
    public void AnInstalledUpdateIsShownOnceAndALaterOutcomeIsShownToo()
    {
        using var machine = new Machine();
        UpdateOutcomeSource source = Source(machine);
        Assert.IsTrue(machine.Store.WriteUpdateOutcome(new(IdA, Now, UpdateOutcomeKind.Installed, "1.2.0", "", "")).Ok);

        StaThread.Run(() =>
        {
            using var first = new UpdateTrayHarness(outcomeSource: source);
            first.Settle();
            Assert.AreEqual("Earshot was updated to 1.2.0.", first.Cards.Shown.Single().Content.Status);
        });

        Assert.IsTrue(machine.Store.WriteUpdateOutcome(new(IdB, Now.AddDays(1), UpdateOutcomeKind.Installed, "1.3.0", "", "")).Ok);
        StaThread.Run(() =>
        {
            using var second = new UpdateTrayHarness(outcomeSource: source);
            second.Settle();
            Assert.AreEqual("Earshot was updated to 1.3.0.", second.Cards.Shown.Single().Content.Status);
        });
    }

    [TestMethod]
    public void AnInstallStillInItsWindowIsLeftForTheNextStart()
    {
        using var machine = new Machine();
        UpdateOutcomeSource source = Source(machine);
        var installing = new UpdateOutcome(IdA, new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero), UpdateOutcomeKind.Installing, "", "", "");
        Assert.IsTrue(machine.Store.WriteUpdateOutcome(installing).Ok);

        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(outcomeSource: source);
            tray.Settle();
            Assert.IsEmpty(tray.Cards.Shown);
            Assert.IsFalse(File.Exists(source.ShownFile), "Nothing was shown, so nothing is noted as shown.");
        });
    }

    [TestMethod]
    public void NoFileAndAFileThatCannotBeReadShowNothingAndTheSecondSaysWhy()
    {
        using var machine = new Machine();
        UpdateOutcomeSource source = Source(machine);

        StaThread.Run(() =>
        {
            using var none = new UpdateTrayHarness(outcomeSource: source);
            none.Settle();
            Assert.IsEmpty(none.Cards.Shown);
        });

        File.WriteAllText(machine.Store.UpdateOutcomeFile, "not json");
        StaThread.Run(() =>
        {
            using var bad = new UpdateTrayHarness(outcomeSource: source);
            bad.Settle();
            Assert.IsEmpty(bad.Cards.Shown);
            Assert.IsTrue(bad.Log.Has(LogLevel.Warn, "could not be read"));
        });
    }

    [TestMethod]
    public void ATrayWithNoSourceReadsNothing()
    {
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();

            Assert.IsEmpty(tray.Cards.Shown);
        });
    }
}

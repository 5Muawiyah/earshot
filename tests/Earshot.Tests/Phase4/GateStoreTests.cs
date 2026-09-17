using System.Globalization;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class GateStoreTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void ConfigDeviceAndProtectionRoundTrip()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        var hfp = new Guid("0000111E-0000-1000-8000-00805F9B34FB");

        Assert.IsTrue(store.WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);
        Assert.IsTrue(store.WriteDevice(RecordedNodes.AirPods()).Ok);
        Assert.IsTrue(store.WriteProtection(new ProtectionRecord { DisabledServices = { hfp } }).Ok);

        GateRead<GateConfig> config = store.ReadConfig();
        GateRead<DeviceIdentity> device = store.ReadDevice();
        GateRead<ProtectionRecord> protection = store.ReadProtection();
        Assert.IsTrue(config.IsOk);
        Assert.IsFalse(config.Value!.BlockAtBoot);
        Assert.IsTrue(device.IsOk);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, device.Value!.Address);
        Assert.AreEqual(RecordedNodes.AirPodsContainer, device.Value.ContainerId);
        Assert.IsTrue(protection.IsOk);
        CollectionAssert.AreEqual(new[] { hfp }, protection.Value!.DisabledServices);
        Assert.IsEmpty(Directory.GetFiles(temp.Path, "*.tmp-*"), "No temporary file is left behind.");
    }

    [TestMethod]
    public void MissingFilesReadAsMissing()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);

        Assert.AreEqual(GateReadStatus.Missing, store.ReadConfig().Status);
        Assert.AreEqual(GateReadStatus.Missing, store.ReadDevice().Status);
        Assert.AreEqual(GateReadStatus.Missing, store.ReadProtection().Status);
        Assert.AreEqual(GateReadStatus.Missing, store.ReadStatus(Nonce).Status);
        Assert.IsFalse(store.ReadDevice().Step.Ok);
        Assert.AreEqual(GateReadStatus.Missing, new GateStore(Path.Combine(temp.Path, "absent")).ReadDevice().Status);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\",\"Extra\":1}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"Address\":\"5A6b7C8d9Eaf\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF0\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"Address\":300,\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"00000000-0000-0000-ffff-ffffffffffff\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"{1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d}\"}")]
    [DataRow("{\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\",}")]
    [DataRow("{/* c */\"Address\":\"5A6B7C8D9EAF\",\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    [DataRow("{\"Address\":null,\"ContainerId\":\"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\"}")]
    public void AnythingButAValidDeviceIsInvalid(string content)
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.DeviceFile, content);

        GateRead<DeviceIdentity> read = store.ReadDevice();

        Assert.AreEqual(GateReadStatus.Invalid, read.Status);
        Assert.IsNull(read.Value);
        Assert.IsFalse(read.Step.Ok);
        Assert.AreEqual("0x8007000D", read.Step.CodeName);
    }

    [TestMethod]
    public void ACorrectDeviceFileWrittenByHandReads()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.DeviceFile, "{ \"Address\": \"5A6B7C8D9EAF\", \"ContainerId\": \"1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D\" }");

        Assert.IsTrue(store.ReadDevice().IsOk);
    }

    [TestMethod]
    [DataRow("{\"SchemaVersion\":1}")]
    [DataRow("{\"BlockAtBoot\":true}")]
    [DataRow("{\"SchemaVersion\":2,\"BlockAtBoot\":true}")]
    [DataRow("{\"SchemaVersion\":0,\"BlockAtBoot\":true}")]
    [DataRow("{\"SchemaVersion\":1.5,\"BlockAtBoot\":true}")]
    [DataRow("{\"SchemaVersion\":1,\"BlockAtBoot\":\"true\"}")]
    [DataRow("{\"SchemaVersion\":1,\"BlockAtBoot\":1}")]
    [DataRow("{\"SchemaVersion\":1,\"BlockAtBoot\":true,\"BlockAtBoot\":false}")]
    public void AConfigWithoutExactlyTheTwoMembersIsInvalid(string content)
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.ConfigFile, content);

        Assert.AreEqual(GateReadStatus.Invalid, store.ReadConfig().Status);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(true)]
    [DataRow(false)]
    public void APendingProtectRequestRoundTripsAndIsLeftOutWhenNone(bool? pending)
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        var hfp = new Guid("0000111E-0000-1000-8000-00805F9B34FB");

        Assert.IsTrue(store.WriteProtection(new ProtectionRecord { DisabledServices = { hfp }, PendingProtect = pending }).Ok);
        GateRead<ProtectionRecord> read = store.ReadProtection();

        Assert.IsTrue(read.IsOk);
        Assert.AreEqual(pending, read.Value!.PendingProtect);
        CollectionAssert.AreEqual(new[] { hfp }, read.Value.DisabledServices);
        Assert.AreEqual(pending.HasValue, File.ReadAllText(store.ProtectionFile).Contains("PendingProtect", StringComparison.Ordinal));
    }

    // A record written before PendingProtect existed still reads, with no request pending.
    [TestMethod]
    public void AnOlderProtectionRecordWithoutPendingProtectReads()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.ProtectionFile, "{\"DisabledServices\":[\"0000111e-0000-1000-8000-00805f9b34fb\"]}");

        GateRead<ProtectionRecord> read = store.ReadProtection();

        Assert.IsTrue(read.IsOk);
        Assert.IsNull(read.Value!.PendingProtect);
        Assert.HasCount(1, read.Value.DisabledServices);
    }

    [TestMethod]
    [DataRow("{\"DisabledServices\":[],\"PendingProtect\":null}")]
    [DataRow("{\"DisabledServices\":[],\"PendingProtect\":\"true\"}")]
    [DataRow("{\"DisabledServices\":[],\"PendingProtect\":1}")]
    [DataRow("{\"DisabledServices\":[],\"PendingProtect\":true,\"PendingProtect\":false}")]
    [DataRow("{\"PendingProtect\":true}")]
    public void AnInvalidPendingProtectIsInvalid(string content)
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.ProtectionFile, content);

        Assert.AreEqual(GateReadStatus.Invalid, store.ReadProtection().Status);
    }

    [TestMethod]
    [DataRow("{\"DisabledServices\":[\"nope\"]}")]
    [DataRow("{\"DisabledServices\":[\"00000000-0000-0000-0000-000000000000\"]}")]
    [DataRow("{\"DisabledServices\":[\"0000111e-0000-1000-8000-00805f9b34fb\",\"0000111e-0000-1000-8000-00805f9b34fb\"]}")]
    [DataRow("{\"DisabledServices\":[1]}")]
    [DataRow("{\"DisabledServices\":{}}")]
    public void AnInvalidProtectionRecordIsInvalid(string content)
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.ProtectionFile, content);

        Assert.AreEqual(GateReadStatus.Invalid, store.ReadProtection().Status);
    }

    [TestMethod]
    public void AnOversizedFileIsNotParsed()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        File.WriteAllText(store.ConfigFile, "{\"SchemaVersion\":1,\"BlockAtBoot\":true}" + new string(' ', GateStore.MaxFileBytes));

        GateRead<GateConfig> read = store.ReadConfig();

        Assert.AreEqual(GateReadStatus.Invalid, read.Status);
        StringAssert.Contains(read.Step.Detail, "larger than");
    }

    [TestMethod]
    public void WritingAnInvalidIdentityWritesNothing()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);

        StepOutcome step = store.WriteDevice(new DeviceIdentity { Address = "5A6b7C8d9Eaf", ContainerId = RecordedNodes.AirPodsContainer });

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, step.Code);
        Assert.IsFalse(File.Exists(store.DeviceFile));
    }

    private static GateStatusFile Status(string nonce, IReadOnlyList<StepOutcome> steps, string verb = GateVerbs.Block) =>
        new(1, nonce, verb, new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 15, 10, 0, 1, TimeSpan.Zero),
            "success", 0, nameof(BlockState.Blocked), steps, false);

    [TestMethod]
    public void AStatusFileRoundTrips()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        StepOutcome[] steps = [StepOutcomes.FromConfigRet("cm-disable:BTHENUM\\x", 0x28, "Windows does not allow this node to be disabled.")];

        Assert.IsTrue(store.WriteStatus(Status(Nonce, steps)).Ok);
        GateRead<GateStatusFile> read = store.ReadStatus(Nonce);

        Assert.IsTrue(read.IsOk, read.Step.Detail);
        Assert.AreEqual(Nonce, read.Value!.Nonce);
        Assert.AreEqual(GateVerbs.Block, read.Value.Verb);
        Assert.AreEqual("Blocked", read.Value.State);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 15, 10, 0, 1, TimeSpan.Zero), read.Value.FinishedUtc);
        Assert.HasCount(1, read.Value.Steps);
        Assert.AreEqual(steps[0], read.Value.Steps[0]);
        Assert.IsFalse(read.Value.StepsTruncated);
    }

    [TestMethod]
    public void StatusStepsAndTextsAreBounded()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        List<StepOutcome> many = Enumerable.Range(0, 200)
            .Select(i => StepOutcomes.FromConfigRet("step-" + i.ToString(CultureInfo.InvariantCulture), 0, new string('x', 5000) + "\u0007"))
            .ToList();

        Assert.IsTrue(store.WriteStatus(Status(Nonce, many)).Ok);
        GateRead<GateStatusFile> read = store.ReadStatus(Nonce);

        Assert.IsTrue(read.IsOk, read.Step.Detail);
        Assert.HasCount(GateStore.MaxStatusSteps, read.Value!.Steps);
        Assert.IsTrue(read.Value.StepsTruncated);
        Assert.IsTrue(read.Value.Steps.All(s => s.Detail!.Length == GateStore.MaxStepText));
        Assert.IsLessThanOrEqualTo(GateStore.MaxFileBytes, new FileInfo(store.StatusFile(Nonce)).Length);
    }

    [TestMethod]
    public void AStatusThatWouldBeTooLargeIsShortenedNotLost()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        List<StepOutcome> escaped = Enumerable.Range(0, GateStore.MaxStatusSteps)
            .Select(i => StepOutcomes.FromConfigRet("step-" + i.ToString(CultureInfo.InvariantCulture), 0, new string('\u2019', 1000) + "\uD83D\uDE00"))
            .ToList();

        StepOutcome written = store.WriteStatus(Status(Nonce, escaped));
        GateRead<GateStatusFile> read = store.ReadStatus(Nonce);

        Assert.IsTrue(written.Ok, written.Detail);
        Assert.IsTrue(read.IsOk, read.Step.Detail);
        Assert.IsTrue(read.Value!.StepsTruncated);
        Assert.HasCount(GateStore.MaxStatusSteps, read.Value.Steps);
        Assert.IsTrue(read.Value.Steps.All(s => s.Detail!.Length == 80));
    }

    [TestMethod]
    public void SurrogatesAndControlCharactersNeverBreakTheJson()
    {
        Assert.AreEqual("a b??c", GateStore.Bound("a\nb\uD83D\uDE00c"));
        Assert.AreEqual("ab?", GateStore.Bound("ab\uD83D\uDE00", limit: 3));
        Assert.AreEqual("?", GateStore.Bound("\uDE00"));
    }

    [TestMethod]
    public void AStatusFileForAnotherNonceOrVerbIsInvalid()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        const string other = "ffffffffffffffffffffffffffffffff";
        Assert.IsTrue(store.WriteStatus(Status(Nonce, [])).Ok);
        File.Copy(store.StatusFile(Nonce), store.StatusFile(other));

        Assert.AreEqual(GateReadStatus.Invalid, store.ReadStatus(other).Status);

        string text = File.ReadAllText(store.StatusFile(Nonce)).Replace("\"block\"", "\"install\"", StringComparison.Ordinal);
        File.WriteAllText(store.StatusFile(Nonce), text);
        Assert.AreEqual(GateReadStatus.Invalid, store.ReadStatus(Nonce).Status);
    }

    [TestMethod]
    [DataRow("../x")]
    [DataRow("0123456789ABCDEF0123456789ABCDEF")]
    [DataRow("")]
    public void AStatusPathNeedsAValidNonce(string nonce)
    {
        var store = new GateStore(Path.GetTempPath());

        Assert.ThrowsExactly<ArgumentException>(() => store.StatusFile(nonce));
    }

    [TestMethod]
    public void PruneKeepsTheNewestStatusFilesAndOnlyTouchesItsOwnNames()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 20; i++)
        {
            string nonce = i.ToString("x32", CultureInfo.InvariantCulture);
            string path = store.StatusFile(nonce);
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-i));
        }

        File.WriteAllText(Path.Combine(temp.Path, "config.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "status-notanonce.json"), "{}");
        string staleTemp = Path.Combine(temp.Path, "device.json.tmp-" + new string('a', 32));
        string freshTemp = Path.Combine(temp.Path, "config.json.tmp-" + new string('b', 32));
        string staleIntentTemp = Path.Combine(temp.Path, "protection-intent.json.tmp-" + new string('c', 32));
        File.WriteAllText(staleTemp, "");
        File.SetLastWriteTimeUtc(staleTemp, now.AddHours(-2));
        File.WriteAllText(staleIntentTemp, "");
        File.SetLastWriteTimeUtc(staleIntentTemp, now.AddHours(-2));
        File.WriteAllText(freshTemp, "");
        File.SetLastWriteTimeUtc(freshTemp, now.AddMinutes(-5));

        IReadOnlyList<StepOutcome> steps = store.PruneStatusFiles(new DateTimeOffset(now));

        Assert.IsEmpty(steps);
        string[] kept = Directory.GetFiles(temp.Path, "status-*.json").Select(Path.GetFileName).ToArray()!;
        Assert.HasCount(GateStore.MaxStatusFilesKept - 1 + 1, kept, "15 status files plus the name that is not a nonce.");
        for (int i = 0; i < GateStore.MaxStatusFilesKept - 1; i++)
        {
            CollectionAssert.Contains(kept, "status-" + i.ToString("x32", CultureInfo.InvariantCulture) + ".json");
        }

        Assert.IsTrue(File.Exists(Path.Combine(temp.Path, "config.json")));
        Assert.IsFalse(File.Exists(staleTemp));
        Assert.IsFalse(File.Exists(staleIntentTemp), "A kept protection request's temporary file left by a crash is pruned too.");
        Assert.IsTrue(File.Exists(freshTemp));
    }

    [TestMethod]
    public void AReaderHoldingTheFileDoesNotBlockTheReplace()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        Assert.IsTrue(store.WriteConfig(new GateConfig { BlockAtBoot = true }).Ok);

        // A reader holds the file (with the share mode the store reads with) for a moment while the write runs.
        var reader = new FileStream(store.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var release = new Timer(_ => reader.Dispose(), null, TimeSpan.FromMilliseconds(40), Timeout.InfiniteTimeSpan);

        Assert.IsTrue(store.WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);

        Assert.IsFalse(store.ReadConfig().Value!.BlockAtBoot);
    }

    [TestMethod]
    public void AWriteThatCannotReplaceTheTargetIsAFailedStep()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        Directory.CreateDirectory(store.StatusFile(Nonce));

        StepOutcome step = store.WriteStatus(Status(Nonce, []));

        Assert.IsFalse(step.Ok);
        Assert.IsLessThan(0, step.Code);
        Assert.IsEmpty(Directory.GetFiles(temp.Path, "*.tmp-*"), "The temporary file is removed.");
    }
}

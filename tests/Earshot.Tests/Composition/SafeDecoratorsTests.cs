using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Composition;

[TestClass]
public sealed class SafeDecoratorsTests
{
    private static readonly string[] ReadCallsBlock = ["IsSetUp", "GetStatusAsync"];
    private static readonly string[] ReadCallsProtection = ["GetStatusAsync", "GetPendingProtectAsync"];

    // Records every call. Live members fail the test if they are ever reached.
    private sealed class RecordingBlock : IBlockController
    {
        public static readonly BootBlockStatus Status = new(BlockState.Blocked, Guid.NewGuid(), Array.Empty<BluetoothNode>(), true, true);

        public List<string> Calls { get; } = new();

        public bool IsSetUp
        {
            get
            {
                Calls.Add("IsSetUp");
                return true;
            }
        }

        public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default)
        {
            Calls.Add("GetStatusAsync");
            return Task.FromResult(Status);
        }

        public Task<ControllerResult> BlockAsync(CancellationToken ct = default) => Live();

        public Task<ControllerResult> AllowAsync(CancellationToken ct = default) => Live();

        public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default) => Live();

        public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default) => Live();

        public Task<ControllerResult> RunSetupAsync(CancellationToken ct = default) => Live();

        public Task<ControllerResult> UninstallAsync(CancellationToken ct = default) => Live();

        private Task<ControllerResult> Live()
        {
            Calls.Add("LIVE");
            throw new AssertFailedException("A live block action reached the real controller in safe mode.");
        }
    }

    private sealed class RecordingConnection : IConnectionController
    {
        public int Calls { get; private set; }

        public Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default) => Live();

        public Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default) => Live();

        private Task<ConnectResult> Live()
        {
            Calls++;
            throw new AssertFailedException("A live connection action reached the real controller in safe mode.");
        }
    }

    private sealed class RecordingProtection : IAudioProtectionController
    {
        public static readonly AudioProtectionSnapshot Status = new(AudioProtectionState.Protected, false, false);

        public List<string> Calls { get; } = new();

        public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default)
        {
            Calls.Add("GetStatusAsync");
            return Task.FromResult(Status);
        }

        public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default)
        {
            Calls.Add("LIVE");
            throw new AssertFailedException("A live protection action reached the real controller in safe mode.");
        }

        public Task<bool?> GetPendingProtectAsync(CancellationToken ct = default)
        {
            Calls.Add("GetPendingProtectAsync");
            return Task.FromResult<bool?>(true);
        }
    }

    private static void AssertRefused(ControllerResult result)
    {
        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual("Safe mode: no device actions.", result.UserMessage);
        Assert.IsFalse(result.IsSuccess);
        Assert.HasCount(1, result.Steps);
        AssertNotAttemptedStep(result.Steps[0]);
    }

    // A refusal made no native call, so its code must never read as S_OK or as any Windows code.
    private static void AssertNotAttemptedStep(StepOutcome step)
    {
        Assert.IsFalse(step.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, step.Code);
        Assert.IsLessThan(0, step.Code);
        Assert.AreEqual("NOT_ATTEMPTED", step.CodeName);
        Assert.AreEqual(NativeCodes.Name(step.Code), step.CodeName);
        Assert.IsTrue(step.Step.StartsWith("safe-mode:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BlockActionsAreRefusedAndReadsPassThrough()
    {
        var log = new CapturingLog();
        var inner = new RecordingBlock();
        IBlockController safe = SafeDecorators.Wrap(inner, log);

        Assert.IsTrue(safe.IsSetUp);
        Assert.AreSame(RecordingBlock.Status, await safe.GetStatusAsync());

        AssertRefused(await safe.BlockAsync());
        AssertRefused(await safe.AllowAsync());
        AssertRefused(await safe.SetBlockAtBootAsync(true));
        AssertRefused(await safe.SetBlockAtBootAsync(false));
        AssertRefused(await safe.SetDeviceAsync("5A6B7C8D9EAF"));
        AssertRefused(await safe.RunSetupAsync());
        AssertRefused(await safe.UninstallAsync());

        CollectionAssert.AreEqual(ReadCallsBlock, inner.Calls);
        Assert.AreEqual(7, log.Entries.Count(e => e.Level == LogLevel.Warn && e.Message.Contains("Safe mode: no device actions.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ConnectionActionsAreRefused()
    {
        var log = new CapturingLog();
        var inner = new RecordingConnection();
        IConnectionController safe = SafeDecorators.Wrap(inner, log);

        foreach (ConnectResult result in new[] { await safe.ConnectAsync(Guid.NewGuid()), await safe.DisconnectAsync(Guid.NewGuid()) })
        {
            Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
            Assert.AreEqual("Safe mode: no device actions.", result.UserMessage);
            Assert.IsFalse(result.Confirmed);
            Assert.HasCount(1, result.Steps);
            AssertNotAttemptedStep(result.Steps[0]);
        }

        Assert.AreEqual(0, inner.Calls);
        Assert.AreEqual(2, log.Entries.Count(e => e.Level == LogLevel.Warn));
    }

    [TestMethod]
    public async Task ProtectionApplyIsRefusedAndStatusPassesThrough()
    {
        var log = new CapturingLog();
        var inner = new RecordingProtection();
        IAudioProtectionController safe = SafeDecorators.Wrap(inner, log);

        Assert.AreSame(RecordingProtection.Status, await safe.GetStatusAsync());
        Assert.IsTrue(await safe.GetPendingProtectAsync(), "The kept request is a read and passes through.");
        AssertRefused(await safe.ApplyAsync(true));
        AssertRefused(await safe.ApplyAsync(false));

        CollectionAssert.AreEqual(ReadCallsProtection, inner.Calls);
    }

    [TestMethod]
    public void WrapIsIdempotent()
    {
        var log = new CapturingLog();
        IBlockController once = SafeDecorators.Wrap(new RecordingBlock(), log);
        IConnectionController connection = SafeDecorators.Wrap(new RecordingConnection(), log);
        IAudioProtectionController protection = SafeDecorators.Wrap(new RecordingProtection(), log);

        Assert.AreSame(once, SafeDecorators.Wrap(once, log));
        Assert.AreSame(connection, SafeDecorators.Wrap(connection, log));
        Assert.AreSame(protection, SafeDecorators.Wrap(protection, log));
    }

    [TestMethod]
    public async Task SafeRegistryWrapsEveryAssignment()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();
        var registry = new ServiceRegistry(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: true);
        var block = new RecordingBlock();

        registry.Block = block;
        registry.Connection = new RecordingConnection();
        registry.Protection = new RecordingProtection();

        Assert.IsInstanceOfType<SafeBlockController>(registry.Block);
        Assert.IsInstanceOfType<SafeConnectionController>(registry.Connection);
        Assert.IsInstanceOfType<SafeAudioProtectionController>(registry.Protection);
        AssertRefused(await registry.Block.BlockAsync());
        Assert.AreSame(block, ((SafeBlockController)registry.Block).Inner);
    }

    // Tests only build the registry in safe mode, and release whatever a feature hook created.
    private static void Release(ServiceRegistry registry)
    {
        registry.Monitor.Dispose();
        registry.Worker?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [TestMethod]
    public void BuildInSafeModeWrapsTheControllers()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();

        ServiceRegistry registry = CompositionRoot.Build(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: true);
        try
        {
            Assert.IsTrue(registry.SafeMode);
            Assert.IsInstanceOfType<SafeConnectionController>(registry.Connection);
            Assert.IsInstanceOfType<SafeBlockController>(registry.Block);
            Assert.IsInstanceOfType<SafeAudioProtectionController>(registry.Protection);
            Assert.IsNotInstanceOfType<SafeBlockController>(((SafeBlockController)registry.Block).Inner, "Wrapped once, not twice.");
            Assert.IsFalse(registry.Battery.HasSource);
            Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode is on."));
        }
        finally
        {
            Release(registry);
        }
    }

    [TestMethod]
    public void BuildReadsSafeModeFromTheEnvironment()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);

        using (new EnvironmentVariableScope(Paths.SafeModeVariable, "1"))
        {
            ServiceRegistry registry = CompositionRoot.Build(log, settings, a => a());
            try
            {
                Assert.IsTrue(registry.SafeMode);
                Assert.IsInstanceOfType<SafeBlockController>(registry.Block);
            }
            finally
            {
                Release(registry);
            }
        }
    }

    [TestMethod]
    public async Task RegistryWithoutSafeModeDoesNotWrap()
    {
        var log = new CapturingLog();
        using var temp = new TempFolder();
        var registry = new ServiceRegistry(log, new JsonSettingsStore(temp.File("settings.json"), log), a => a(), safeMode: false);
        var protection = new RecordingProtection();

        registry.Protection = protection;

        Assert.IsFalse(registry.SafeMode);
        Assert.AreSame(protection, registry.Protection);
        Assert.IsNotInstanceOfType<SafeConnectionController>(registry.Connection);
        Assert.IsNotInstanceOfType<SafeBlockController>(registry.Block);
        Assert.AreSame(RecordingProtection.Status, await registry.Protection.GetStatusAsync());
    }
}

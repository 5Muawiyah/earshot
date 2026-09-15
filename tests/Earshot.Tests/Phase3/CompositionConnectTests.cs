using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Tests.Phase2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase3;

// The connect hook: the real controller when the audio hook made a worker and a monitor, wrapped in safe mode,
// and the null controller with a logged reason otherwise. Building the registry starts no thread and sends nothing.
[TestClass]
public sealed class CompositionConnectTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task TheRegistryGetsTheConnectionController()
    {
        var log = new CapturingLog();
        ServiceRegistry registry = CompositionRoot.Build(log, new FakeSettingsStore(), a => a(), safeMode: false);
        try
        {
            Assert.IsInstanceOfType<ConnectionController>(registry.Connection);
            Assert.IsInstanceOfType<AudioWorker>(registry.Worker);
            Assert.IsFalse(((AudioWorker)registry.Worker).HasStarted, "Wiring starts nothing.");
        }
        finally
        {
            registry.Monitor.Dispose();
            await registry.Worker!.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SafeModeWrapsTheControllerAndNothingRuns()
    {
        var log = new CapturingLog();
        ServiceRegistry registry = CompositionRoot.Build(log, new FakeSettingsStore(), a => a(), safeMode: true);
        try
        {
            SafeConnectionController safe = Assert.IsInstanceOfType<SafeConnectionController>(registry.Connection);
            Assert.IsInstanceOfType<ConnectionController>(safe.Inner);

            ConnectResult result = await registry.Connection.ConnectAsync(AirPodsContainer).WaitAsync(Guard);

            Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
            Assert.AreEqual(SafeDecorators.Message, result.UserMessage);
            Assert.IsFalse(((AudioWorker)registry.Worker!).HasStarted, "The refused connect never reached the audio worker.");
        }
        finally
        {
            registry.Monitor.Dispose();
            await registry.Worker!.DisposeAsync();
        }
    }

    [TestMethod]
    public void WithoutAnAudioWorkerTheNullControllerStays()
    {
        var log = new CapturingLog();
        var registry = new ServiceRegistry(log, new FakeSettingsStore(), a => a(), safeMode: false);

        CompositionRoot.AddConnection(registry);

        Assert.IsInstanceOfType<NullConnectionController>(registry.Connection);
        Assert.IsTrue(log.Has(LogLevel.Warn, "no audio worker was created"));
    }

    [TestMethod]
    public async Task WithoutADeviceMonitorTheNullControllerStays()
    {
        var log = new CapturingLog();
        var registry = new ServiceRegistry(log, new FakeSettingsStore(), a => a(), safeMode: false);
        await using var worker = new AudioWorker(log);
        registry.Worker = worker;

        CompositionRoot.AddConnection(registry);

        Assert.IsInstanceOfType<NullConnectionController>(registry.Connection);
        Assert.IsTrue(log.Has(LogLevel.Warn, "no device monitor"));
    }
}

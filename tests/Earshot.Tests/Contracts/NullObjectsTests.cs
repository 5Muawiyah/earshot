using Earshot.Contracts;
using Earshot.Contracts.Null;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

[TestClass]
public sealed class NullObjectsTests
{
    [TestMethod]
    public async Task NullConnectionControllerReportsNotAvailable()
    {
        var controller = new NullConnectionController();

        ConnectResult connect = await controller.ConnectAsync(Guid.NewGuid());
        ConnectResult disconnect = await controller.DisconnectAsync(Guid.NewGuid());

        foreach (ConnectResult result in new[] { connect, disconnect })
        {
            Assert.AreEqual(ConnectOutcome.Failed, result.Outcome);
            Assert.IsFalse(result.Confirmed);
            Assert.AreEqual(NullResults.NotAvailableMessage, result.UserMessage);
            Assert.HasCount(1, result.Steps);
            Assert.IsFalse(result.Steps[0].Ok);
        }
    }

    [TestMethod]
    public async Task NullBlockControllerIsNotSetUpAndRefusesActions()
    {
        var controller = new NullBlockController();

        Assert.IsFalse(controller.IsSetUp);
        BootBlockStatus status = await controller.GetStatusAsync();
        Assert.AreEqual(BlockState.NotSetUp, status.State);
        Assert.IsFalse(status.TasksInstalled);
        Assert.IsTrue(status.BlockAtBoot, "Before setup the shipped default (on) is shown.");
        Assert.IsEmpty(status.Nodes);

        ControllerResult[] results =
        [
            await controller.BlockAsync(),
            await controller.AllowAsync(),
            await controller.SetBlockAtBootAsync(true),
            await controller.SetBlockAtBootAsync(false),
            await controller.SetDeviceAsync("5A6B7C8D9EAF"),
            await controller.RunSetupAsync(),
            await controller.UninstallAsync(),
        ];
        foreach (ControllerResult result in results)
        {
            Assert.AreEqual(OpStatus.NotAttempted, result.Status);
            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(NullResults.NotAvailableMessage, result.UserMessage);
        }
    }

    [TestMethod]
    public async Task NullAudioProtectionControllerIsUnknownAndRefusesApply()
    {
        var controller = new NullAudioProtectionController();

        AudioProtectionSnapshot status = await controller.GetStatusAsync();
        Assert.AreEqual(AudioProtectionState.Unknown, status.State);

        ControllerResult result = await controller.ApplyAsync(true);
        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
    }

    [TestMethod]
    public async Task NullDeviceMonitorFindsNothing()
    {
        using var monitor = new NullDeviceMonitor();
        int raised = 0;
        EventHandler<DeviceSnapshotEventArgs> handler = (_, _) => raised++;
        monitor.SnapshotChanged += handler;

        monitor.Start();
        DeviceSnapshot refreshed = await monitor.RefreshAsync();
        monitor.SnapshotChanged -= handler;

        Assert.IsNull(monitor.Current.Target);
        Assert.IsEmpty(monitor.Current.AllGroups);
        Assert.AreSame(monitor.Current, refreshed);
        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void NullCardPresenterLogsAtDebugAndShowsNothing()
    {
        var log = new CapturingLog();
        var cards = new NullCardPresenter(log);

        cards.Show(new CardContent("Owner\u2019s AirPods Pro", "Connected"), CardAnchor.NearCursor);
        cards.Hide();

        Assert.HasCount(1, log.Entries);
        Assert.AreEqual(LogLevel.Debug, log.Entries[0].Level);
        Assert.IsTrue(log.Entries[0].Message.Contains("Connected", StringComparison.Ordinal));
    }
}

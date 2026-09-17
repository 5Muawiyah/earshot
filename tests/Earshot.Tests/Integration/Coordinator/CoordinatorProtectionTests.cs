using Earshot.App;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The coordinator must see the protection request the gate keeps (protection-intent.json), or a restore chosen
// while the AirPods were blocked is lost at the next restart. These pin that the controller the tray hands it can
// report one, whatever the registry holds.
[TestClass]
public sealed class CoordinatorProtectionTests
{
    [TestMethod]
    public void TheRealProtectionControllerCanReportAKeptRequest()
    {
        Assert.IsTrue(CoordinatorProtection.CanReadKeptRequest(typeof(AudioProtectionController)),
            "The coordinator would never see a request the gate kept.");
    }

    [TestMethod]
    public void AControllerThatReportsTheRequestItselfIsUsedAsItIs()
    {
        var controller = new FakeProtection { Pending = true };

        Assert.IsTrue(CoordinatorProtection.ReportsKeptRequest(typeof(FakeProtection)));
        Assert.AreSame(controller, CoordinatorProtection.For(controller));
    }

    [TestMethod]
    public void ADefaultOnlyControllerIsNotTakenForOneThatReports()
    {
        Assert.IsFalse(CoordinatorProtection.ReportsKeptRequest(typeof(DefaultOnly)));
        Assert.IsFalse(CoordinatorProtection.CanReadKeptRequest(typeof(DefaultOnly)));
    }

    [TestMethod]
    public async Task TheSafeModeWrapperStillReportsTheKeptRequestAndStillRefusesChanges()
    {
        var inner = new FakeProtection { Pending = false };
        var safe = new SafeAudioProtectionController(inner, new CapturingLog());

        IAudioProtectionController controller = CoordinatorProtection.For(safe);

        Assert.IsFalse(await controller.GetPendingProtectAsync());
        ControllerResult change = await controller.ApplyAsync(true);
        Assert.AreEqual(OpStatus.NotAttempted, change.Status);
        Assert.IsEmpty(inner.Applies, "Safe mode let a change through.");
    }

    [TestMethod]
    public void TheGateFileBecomesTheKeptRequest()
    {
        using var folder = new TempFolder();
        var file = new ProtectionIntentFile(folder.Path);

        Assert.IsNull(CoordinatorProtection.KeptRequest(file.Read()), "Nothing kept.");

        Assert.IsTrue(file.Write(false).Ok);
        Assert.IsFalse(CoordinatorProtection.KeptRequest(file.Read()));

        Assert.IsTrue(file.Write(true).Ok);
        Assert.IsTrue(CoordinatorProtection.KeptRequest(file.Read()));
    }

    [TestMethod]
    public void AGateFileThatIsNotValidIsAnErrorWithItsCode()
    {
        using var folder = new TempFolder();
        var file = new ProtectionIntentFile(folder.Path);
        File.WriteAllText(file.FilePath, "{ not json");

        IOException error = Assert.ThrowsExactly<IOException>(() => CoordinatorProtection.KeptRequest(file.Read()));

        Assert.AreEqual(file.Read().Step.Code, error.HResult);
        Assert.AreNotEqual(0, error.HResult);
    }

    // A controller that takes the interface's default for the kept request.
    private sealed class DefaultOnly : IAudioProtectionController
    {
        public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new AudioProtectionSnapshot(AudioProtectionState.Unknown, HandsfreeInstalled: false, HeadsetInstalled: false));

        public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default) =>
            Task.FromResult(ControllerResult.Ok("Done"));
    }
}

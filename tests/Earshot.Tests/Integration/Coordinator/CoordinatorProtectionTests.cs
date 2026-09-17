using System.Reflection;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The coordinator must see the protection request the gate keeps (protection-intent.json), or a restore chosen
// while the AirPods were blocked is lost at the next restart. The tray hands it the registry's controller as it is,
// so the real controller and the safe-mode wrapper around it must both report the request themselves.
[TestClass]
public sealed class CoordinatorProtectionTests
{
    [TestMethod]
    public void TheRealProtectionControllerReportsAKeptRequestItself()
    {
        Assert.IsTrue(ImplementsKeptRequest(typeof(AudioProtectionController)),
            "The coordinator would never see a request the gate kept.");
    }

    [TestMethod]
    public void TheSafeModeWrapperReportsAKeptRequestItself()
    {
        Assert.IsTrue(ImplementsKeptRequest(typeof(SafeAudioProtectionController)),
            "In safe mode the coordinator would never see a request the gate kept.");
    }

    [TestMethod]
    public void ADefaultOnlyControllerReportsNothing()
    {
        Assert.IsFalse(ImplementsKeptRequest(typeof(DefaultOnly)));
    }

    [TestMethod]
    public async Task TheSafeModeWrapperPassesTheKeptRequestOnAndStillRefusesChanges()
    {
        var inner = new FakeProtection { Pending = false };
        IAudioProtectionController safe = new SafeAudioProtectionController(inner, new CapturingLog());

        Assert.IsFalse(await safe.GetPendingProtectAsync());
        inner.Pending = true;
        Assert.IsTrue(await safe.GetPendingProtectAsync());
        inner.Pending = null;
        Assert.IsNull(await safe.GetPendingProtectAsync());

        ControllerResult change = await safe.ApplyAsync(true);
        Assert.AreEqual(OpStatus.NotAttempted, change.Status);
        Assert.IsEmpty(inner.Applies, "Safe mode let a change through.");
    }

    [TestMethod]
    public void TheGateFileBecomesTheKeptRequest()
    {
        using var folder = new TempFolder();
        var file = new ProtectionIntentFile(folder.Path);

        Assert.IsNull(AudioProtectionController.KeptRequest(file.Read()), "Nothing kept.");

        Assert.IsTrue(file.Write(false).Ok);
        Assert.IsFalse(AudioProtectionController.KeptRequest(file.Read()));

        Assert.IsTrue(file.Write(true).Ok);
        Assert.IsTrue(AudioProtectionController.KeptRequest(file.Read()));
    }

    [TestMethod]
    public void AGateFileThatIsNotValidIsAnErrorWithItsCode()
    {
        using var folder = new TempFolder();
        var file = new ProtectionIntentFile(folder.Path);
        File.WriteAllText(file.FilePath, "{ not json");

        IOException error = Assert.ThrowsExactly<IOException>(() => AudioProtectionController.KeptRequest(file.Read()));

        Assert.AreEqual(file.Read().Step.Code, error.HResult);
        Assert.AreNotEqual(0, error.HResult);
    }

    // True when the type implements GetPendingProtectAsync itself rather than taking the interface's default, which
    // always reports nothing.
    private static bool ImplementsKeptRequest(Type controllerType)
    {
        InterfaceMapping map = controllerType.GetInterfaceMap(typeof(IAudioProtectionController));
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name == nameof(IAudioProtectionController.GetPendingProtectAsync))
            {
                return map.TargetMethods[i].DeclaringType is { IsInterface: false };
            }
        }

        return false;
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

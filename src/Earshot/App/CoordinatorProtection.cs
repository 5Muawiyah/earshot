using System.Reflection;
using Earshot.AudioProtection;
using Earshot.AudioProtection.Gate;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;

namespace Earshot.App;

// The protection controller as the block coordinator sees it: every call goes to the registry's controller, and
// GetPendingProtectAsync reports the request the gate keeps (protection-intent.json) even where that controller
// does not implement it yet. AudioProtectionController reads the file through GetPendingIntentAsync only, and the
// safe-mode wrapper does not pass the member on, so without this the coordinator would never see a request kept
// across a restart (a restore chosen while blocked would be lost). Once both implement GetPendingProtectAsync
// themselves, For returns the controller unchanged.
internal sealed class CoordinatorProtection : IAudioProtectionController
{
    private readonly IAudioProtectionController _inner;
    private readonly Func<CancellationToken, Task<bool?>> _readIntent;

    internal CoordinatorProtection(IAudioProtectionController inner, Func<CancellationToken, Task<bool?>> readIntent)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(readIntent);
        _inner = inner;
        _readIntent = readIntent;
    }

    // The controller to hand the coordinator: controller itself when it reports the kept request, otherwise a
    // wrapper that reads it from the real controller behind any safe-mode wrapper. Reading the file changes
    // nothing, so it is allowed in safe mode.
    public static IAudioProtectionController For(IAudioProtectionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (ReportsKeptRequest(controller.GetType()))
        {
            return controller;
        }

        IAudioProtectionController source = controller is SafeAudioProtectionController safe ? safe.Inner : controller;
        if (ReportsKeptRequest(source.GetType()))
        {
            return new CoordinatorProtection(controller, source.GetPendingProtectAsync);
        }

        return source is AudioProtectionController real
            ? new CoordinatorProtection(controller, async ct => KeptRequest(await real.GetPendingIntentAsync(ct)))
            : controller;
    }

    // True when the coordinator can learn the kept request from a controller of this type, directly or through
    // this wrapper.
    internal static bool CanReadKeptRequest(Type controllerType)
    {
        ArgumentNullException.ThrowIfNull(controllerType);
        return ReportsKeptRequest(controllerType) || typeof(AudioProtectionController).IsAssignableFrom(controllerType);
    }

    // True when the type implements GetPendingProtectAsync itself rather than taking the interface's default,
    // which always reports nothing.
    internal static bool ReportsKeptRequest(Type controllerType)
    {
        ArgumentNullException.ThrowIfNull(controllerType);
        if (!typeof(IAudioProtectionController).IsAssignableFrom(controllerType) || controllerType.IsInterface)
        {
            return false;
        }

        InterfaceMapping map = controllerType.GetInterfaceMap(typeof(IAudioProtectionController));
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name == nameof(IAudioProtectionController.GetPendingProtectAsync))
            {
                MethodInfo target = map.TargetMethods[i];
                return target.DeclaringType is { IsInterface: false };
            }
        }

        return false;
    }

    public Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default) => _inner.GetStatusAsync(ct);

    public Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default) => _inner.ApplyAsync(protect, ct);

    public Task<bool?> GetPendingProtectAsync(CancellationToken ct = default) => _readIntent(ct);

    // True or false for a kept request, null when none is kept. A file that is there but cannot be read or is not
    // valid throws an IOException carrying the read's own code, which the coordinator records and logs.
    internal static bool? KeptRequest(GateRead<ProtectionIntent> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read.Status switch
        {
            GateReadStatus.Ok when read.Value is { } intent => intent.Protect,
            GateReadStatus.Missing => null,
            _ => throw new IOException(
                "The kept protection request could not be read (" + read.Status + "): " + read.Step.CodeName + (read.Step.Detail is null ? "" : ", " + read.Step.Detail),
                read.Step.Code),
        };
    }
}

using System.Runtime.ExceptionServices;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase1;

// Device states for the tray tests, using the ids and names recorded on the owner's PC.
internal static class Phase1Fixtures
{
    public const string AirPodsAddress = "0A1B2C3D4E8C";
    public const string IPhoneAddress = "1A2B3C4D5E6F";
    public const string AirPodsName = "Jonathan\u2019s AirPods Pro";

    public static readonly Guid AirPodsContainer = new("5C3A9E21-4B7D-5F18-9A6C-2D8E0B4F7A13");
    public static readonly Guid IPhoneContainer = new("7E2D4C8A-1B3F-5A6E-B9D0-6C4A2F8E1D35");

    // Nothing has been enumerated yet: not an observation of anything.
    public static DeviceSnapshot NoDevice() =>
        new(Target: null, AllGroups: [], TakenUtc: DateTimeOffset.UnixEpoch);

    // An enumeration that worked and matched nothing.
    public static DeviceSnapshot NotFound() =>
        new(Target: null, AllGroups: [], TakenUtc: DateTimeOffset.UnixEpoch)
        {
            Sequence = 1,
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.NotFound,
        };

    // An enumeration that failed: what it carries is not current.
    public static DeviceSnapshot ReadFailed() =>
        new(Target: null, AllGroups: [], TakenUtc: DateTimeOffset.UnixEpoch)
        {
            ReadStatus = SnapshotReadStatus.Failed,
            Resolution = TargetResolution.ReadFailed,
        };

    public static DeviceSnapshot Target(ConnectionState connection, Guid? container = null, string name = AirPodsName)
    {
        var model = new DeviceModel(container ?? AirPodsContainer, name, connection, []);
        return new DeviceSnapshot(model, [model], DateTimeOffset.UnixEpoch)
        {
            Sequence = 1,
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        };
    }

    // Target(ConnectionState.Connected) carries no endpoints, so CoordinatorRules.RenderOf reads it NotActive:
    // it is only ever a Bluetooth-connection fixture. A hand-back test that must exercise the disconnect step
    // (render ACTIVE) needs a render endpoint too, so this adds one.
    public static DeviceSnapshot TargetRenderActive(Guid? container = null, string name = AirPodsName)
    {
        Guid id = container ?? AirPodsContainer;
        var endpoint = new AudioEndpoint("{0.0.0.00000000}.render", EndpointFlow.Render, EndpointState.Active, name, id);
        var model = new DeviceModel(id, name, ConnectionState.Connected, [endpoint]);
        return new DeviceSnapshot(model, [model], DateTimeOffset.UnixEpoch)
        {
            Sequence = 1,
            ReadStatus = SnapshotReadStatus.Ok,
            Resolution = TargetResolution.Pinned,
        };
    }

    public static BootBlockStatus Block(BlockState state, bool blockAtBoot = true) =>
        new(state, AirPodsContainer, [], TasksInstalled: state != BlockState.NotSetUp, blockAtBoot);

    public static AudioProtectionSnapshot Protection(AudioProtectionState state) =>
        new(state, HandsfreeInstalled: state is AudioProtectionState.NotProtected or AudioProtectionState.Partial, HeadsetInstalled: false);

    public static EarshotSettings Settings(Action<EarshotSettings>? change = null)
    {
        var settings = new EarshotSettings();
        change?.Invoke(settings);
        return settings;
    }
}

// The Run and StartupApproved values in memory. The real HKCU is never read or written by the tests.
internal sealed class FakeStartupRegistry : IStartupRegistry
{
    public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Writes { get; private set; }

    public int Deletes { get; private set; }

    public Exception? ReadFailure { get; set; }

    public Exception? WriteFailure { get; set; }

    public string? ReadRunValue(string name)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Run.TryGetValue(name, out string? value) ? value : null;
    }

    public byte[]? ReadStartupApproved(string name)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Approved.TryGetValue(name, out byte[]? value) ? value : null;
    }

    public void WriteRunValue(string name, string command)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Writes++;
        Run[name] = command;
    }

    public void DeleteRunValue(string name)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Deletes++;
        Run.Remove(name);
    }
}

// Runs WinForms work on a dedicated STA thread and rethrows its exception on the caller.
internal static class StaThread
{
    public static void Run(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Earshot test STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new AssertFailedException("The STA work did not finish within 30 seconds.");
        }

        failure?.Throw();
    }
}

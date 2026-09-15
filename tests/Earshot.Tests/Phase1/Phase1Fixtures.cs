using System.Runtime.ExceptionServices;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase1;

// Device states for the tray tests, using the ids and names recorded on the owner's PC in phase 0.
internal static class Phase1Fixtures
{
    public const string AirPodsAddress = "5A6B7C8D9EAF";
    public const string IPhoneAddress = "3410BE0E0ABB";
    public const string AirPodsName = "Owner\u2019s AirPods Pro";

    public static readonly Guid AirPodsContainer = new("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");
    public static readonly Guid IPhoneContainer = new("4FB94536-5965-549C-A947-0B115F3D9B56");

    public static DeviceSnapshot NoDevice() =>
        new(Target: null, AllGroups: [], TakenUtc: DateTimeOffset.UnixEpoch);

    public static DeviceSnapshot Target(ConnectionState connection, Guid? container = null, string name = AirPodsName)
    {
        var model = new DeviceModel(container ?? AirPodsContainer, name, connection, []);
        return new DeviceSnapshot(model, [model], DateTimeOffset.UnixEpoch);
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

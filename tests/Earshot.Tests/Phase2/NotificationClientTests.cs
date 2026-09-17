using System.Runtime.InteropServices;
using Earshot.Audio;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// The production notification client, called through its COM vtable the way MMDevAPI calls it: each slot
// through an unmanaged function pointer with native arguments, including the PROPERTYKEY passed by value.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
[TestClass]
public sealed class NotificationClientTests
{
    private const string DeviceId = EndpointFixtures.AirPodsRenderId;

    private const int OnDeviceStateChangedSlot = 3;
    private const int OnDeviceAddedSlot = 4;
    private const int OnDeviceRemovedSlot = 5;
    private const int OnDefaultDeviceChangedSlot = 6;
    private const int OnPropertyValueChangedSlot = 7;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StateChangedCall(nint self, nint deviceId, uint newState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceIdCall(nint self, nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DefaultChangedCall(nint self, int flow, int role, nint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyChangedCall(nint self, nint deviceId, PROPERTYKEY key);

    [TestMethod]
    public void EveryCallbackCopiesItsArgumentsAndReturnsSOk()
    {
        var received = new List<EndpointNotification>();
        var client = new NotificationClient(received.Add);
        nint itf = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
        nint id = Marshal.StringToCoTaskMemUni(DeviceId);
        try
        {
            Assert.AreEqual(NotificationClient.S_OK, Slot<StateChangedCall>(itf, OnDeviceStateChangedSlot)(itf, id, CoreAudio.DEVICE_STATE_UNPLUGGED));
            Assert.AreEqual(NotificationClient.S_OK, Slot<DeviceIdCall>(itf, OnDeviceAddedSlot)(itf, id));
            Assert.AreEqual(NotificationClient.S_OK, Slot<DeviceIdCall>(itf, OnDeviceRemovedSlot)(itf, id));
            Assert.AreEqual(NotificationClient.S_OK, Slot<DefaultChangedCall>(itf, OnDefaultDeviceChangedSlot)(itf, CoreAudio.eRender, CoreAudio.eMultimedia, 0));
            Assert.AreEqual(NotificationClient.S_OK, Slot<PropertyChangedCall>(itf, OnPropertyValueChangedSlot)(itf, id, CoreAudio.PKEY_Device_FriendlyName));

            // The native string is freed and overwritten after the call; the copies must not change.
            Marshal.WriteInt16(id, 'X');
        }
        finally
        {
            Marshal.FreeCoTaskMem(id);
            Marshal.Release(itf);
            GC.KeepAlive(client);
        }

        Assert.HasCount(5, received);
        Assert.AreEqual(new EndpointNotification(EndpointNotificationKind.StateChanged, DeviceId, CoreAudio.DEVICE_STATE_UNPLUGGED, 0, 0, Guid.Empty, 0), received[0]);
        Assert.AreEqual(new EndpointNotification(EndpointNotificationKind.Added, DeviceId, 0, 0, 0, Guid.Empty, 0), received[1]);
        Assert.AreEqual(new EndpointNotification(EndpointNotificationKind.Removed, DeviceId, 0, 0, 0, Guid.Empty, 0), received[2]);
        Assert.AreEqual(new EndpointNotification(EndpointNotificationKind.DefaultChanged, null, 0, CoreAudio.eRender, CoreAudio.eMultimedia, Guid.Empty, 0), received[3]);
        Assert.AreEqual(new EndpointNotification(EndpointNotificationKind.PropertyChanged, DeviceId, 0, 0, 0,
            CoreAudio.PKEY_Device_FriendlyName.fmtid, CoreAudio.PKEY_Device_FriendlyName.pid), received[4]);
    }

    [TestMethod]
    public void ASinkFailureIsKeptAndTheCallbackStillReturnsSOk()
    {
        int calls = 0;
        var client = new NotificationClient(_ =>
        {
            calls++;
            throw new InvalidOperationException("queue full " + calls);
        });

        Assert.AreEqual(NotificationClient.S_OK, client.OnDeviceAdded(DeviceId));
        Assert.AreEqual(NotificationClient.S_OK, client.OnDeviceStateChanged(DeviceId, CoreAudio.DEVICE_STATE_ACTIVE));

        (int count, Exception? last) = client.TakeSinkFailures();
        Assert.AreEqual(2, count);
        Assert.IsInstanceOfType<InvalidOperationException>(last);
        Assert.AreEqual("queue full 2", last.Message);

        (int again, Exception? none) = client.TakeSinkFailures();
        Assert.AreEqual(0, again);
        Assert.IsNull(none);
    }

    [TestMethod]
    public void ASinkFailureIsReportedAtOnceAndAFailedReportIsCountedToo()
    {
        int reports = 0;
        var client = new NotificationClient(
            _ => throw new InvalidOperationException("queue full"),
            () =>
            {
                reports++;
                if (reports == 2)
                {
                    throw new NotSupportedException("report could not be queued");
                }
            });

        Assert.AreEqual(NotificationClient.S_OK, client.OnDeviceAdded(DeviceId));
        Assert.AreEqual(NotificationClient.S_OK, client.OnDeviceRemoved(DeviceId));

        Assert.AreEqual(2, reports, "Each failure is reported once.");
        (int count, Exception? last) = client.TakeSinkFailures();
        Assert.AreEqual(3, count, "Two sink failures and one report that could not be queued.");
        Assert.IsInstanceOfType<NotSupportedException>(last);
    }

    [TestMethod]
    public void StateAddAndRemoveRequireARefreshButADefaultChangeDoesNot()
    {
        Assert.IsTrue(Notification(EndpointNotificationKind.StateChanged).RequiresRefresh);
        Assert.IsTrue(Notification(EndpointNotificationKind.Added).RequiresRefresh);
        Assert.IsTrue(Notification(EndpointNotificationKind.Removed).RequiresRefresh);
        Assert.IsFalse(Notification(EndpointNotificationKind.DefaultChanged).RequiresRefresh);
    }

    [TestMethod]
    public void OnlyTheModelPropertiesRequireARefresh()
    {
        Assert.IsTrue(Property(CoreAudio.PKEY_Device_FriendlyName).RequiresRefresh);
        Assert.IsTrue(Property(CoreAudio.PKEY_DeviceInterface_FriendlyName).RequiresRefresh);
        Assert.IsTrue(Property(CoreAudio.PKEY_Device_ContainerId).RequiresRefresh);

        Assert.IsFalse(Property(CoreAudio.PKEY_AudioEndpoint_FormFactor).RequiresRefresh);
        Assert.IsFalse(Property(new PROPERTYKEY(CoreAudio.PKEY_Device_FriendlyName.fmtid, 15)).RequiresRefresh);
        Assert.IsFalse(Property(new PROPERTYKEY(Guid.NewGuid(), 14)).RequiresRefresh);
    }

    private static EndpointNotification Notification(EndpointNotificationKind kind) =>
        new(kind, DeviceId, 0, 0, 0, Guid.Empty, 0);

    private static EndpointNotification Property(PROPERTYKEY key) =>
        new(EndpointNotificationKind.PropertyChanged, DeviceId, 0, 0, 0, key.fmtid, key.pid);

    private static T Slot<T>(nint itf, int slot)
        where T : Delegate
    {
        nint vtable = Marshal.ReadIntPtr(itf);
        nint function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }
}

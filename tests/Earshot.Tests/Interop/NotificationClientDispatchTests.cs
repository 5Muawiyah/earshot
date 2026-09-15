using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// Proves, without a device change, that a managed IMMNotificationClient receives callbacks through its
// COM interface the way MMDevAPI delivers them: each vtable slot is called through an unmanaged function
// pointer with native arguments, including OnPropertyValueChanged's PROPERTYKEY passed by value (a 20
// byte struct, which the x64 calling convention passes as a pointer to a caller-owned copy).
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient
// https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention
[TestClass]
public sealed class NotificationClientDispatchTests
{
    private const string DeviceId = "{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516970}";

    // Slots after IUnknown's three, in SDK header order.
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
    public void EverySlotReachesTheManagedMethodWithItsArguments()
    {
        var client = new RecordingClient();
        nint itf = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
        nint id = Marshal.StringToCoTaskMemUni(DeviceId);
        try
        {
            Assert.AreEqual(0, Slot<StateChangedCall>(itf, OnDeviceStateChangedSlot)(itf, id, CoreAudio.DEVICE_STATE_UNPLUGGED));
            Assert.AreEqual("state " + DeviceId + " " + CoreAudio.DEVICE_STATE_UNPLUGGED, client.Last);

            Assert.AreEqual(0, Slot<DeviceIdCall>(itf, OnDeviceAddedSlot)(itf, id));
            Assert.AreEqual("added " + DeviceId, client.Last);

            Assert.AreEqual(0, Slot<DeviceIdCall>(itf, OnDeviceRemovedSlot)(itf, id));
            Assert.AreEqual("removed " + DeviceId, client.Last);

            DefaultChangedCall defaultChanged = Slot<DefaultChangedCall>(itf, OnDefaultDeviceChangedSlot);
            Assert.AreEqual(0, defaultChanged(itf, CoreAudio.eCapture, CoreAudio.eCommunications, id));
            Assert.AreEqual("default 1 2 " + DeviceId, client.Last);

            // No default device for the flow and role: the id pointer is null.
            Assert.AreEqual(0, defaultChanged(itf, CoreAudio.eRender, CoreAudio.eConsole, 0));
            Assert.AreEqual("default 0 0 (null)", client.Last);

            PROPERTYKEY key = CoreAudio.PKEY_Device_ContainerId;
            Assert.AreEqual(0, Slot<PropertyChangedCall>(itf, OnPropertyValueChangedSlot)(itf, id, key));
            Assert.AreEqual("property " + DeviceId + " " + key.fmtid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + " " + key.pid, client.Last);
        }
        finally
        {
            Marshal.FreeCoTaskMem(id);
            Marshal.Release(itf);
            GC.KeepAlive(client);
        }

        Assert.AreEqual(6, client.Calls);
    }

    private static T Slot<T>(nint itf, int slot)
        where T : Delegate
    {
        nint vtable = Marshal.ReadIntPtr(itf);
        nint function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private sealed class RecordingClient : IMMNotificationClient
    {
        public string Last { get; private set; } = "";

        public int Calls { get; private set; }

        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) =>
            Record("state " + pwstrDeviceId + " " + dwNewState);

        public int OnDeviceAdded(string pwstrDeviceId) => Record("added " + pwstrDeviceId);

        public int OnDeviceRemoved(string pwstrDeviceId) => Record("removed " + pwstrDeviceId);

        public int OnDefaultDeviceChanged(int flow, int role, string? pwstrDefaultDeviceId) =>
            Record("default " + flow + " " + role + " " + (pwstrDefaultDeviceId ?? "(null)"));

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) =>
            Record("property " + pwstrDeviceId + " " + key.fmtid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + " " + key.pid);

        private int Record(string call)
        {
            Last = call;
            Calls++;
            return 0;
        }
    }
}

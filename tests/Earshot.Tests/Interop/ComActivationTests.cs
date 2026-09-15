using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// Object creation and interface wrapping return an HRESULT for every failure instead of throwing.
// The creation tests only create the enumerator and the Task Scheduler service object and release
// them; nothing is enumerated, connected or changed.
[TestClass]
public sealed class ComActivationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Not a registered class on any Windows machine.
    private static readonly Guid UnregisteredClass = new("6A1E3C52-0B7D-4F4B-9F7A-3C1D2E5B8A90");

    [TestMethod]
    public void TakeInterfaceReportsASuccessWithoutAnObject()
    {
        Assert.AreEqual(ComActivation.E_POINTER, ComActivation.TakeInterface<IKsControl>(0, 0, out IKsControl? result));
        Assert.IsNull(result);
        Assert.AreEqual(CoreAudio.E_NOINTERFACE, ComActivation.TakeInterface<IKsControl>(CoreAudio.E_NOINTERFACE, 0, out result));
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TakeInterfaceReturnsTheFailureAndDiscardsAPointerThatCameWithIt()
    {
        var client = new RecordingNotificationClient();
        nint pointer = Marshal.GetIUnknownForObject(client);

        int hr = ComActivation.TakeInterface<IMMNotificationClient>(CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED, pointer, out IMMNotificationClient? result);

        Assert.AreEqual(CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED, hr);
        Assert.IsNull(result);
        GC.KeepAlive(client);
    }

    [TestMethod]
    public void TakeInterfaceGivesENoInterfaceInsteadOfThrowingForAMissingInterface()
    {
        object plain = new();
        nint pointer = Marshal.GetIUnknownForObject(plain);

        int hr = ComActivation.TakeInterface<IKsControl>(0, pointer, out IKsControl? result);

        Assert.AreEqual(CoreAudio.E_NOINTERFACE, hr, NativeCodes.Name(hr));
        Assert.IsNull(result);
        GC.KeepAlive(plain);
    }

    [TestMethod]
    public void TakeInterfaceWrapsAnObjectThatHasTheInterface()
    {
        var client = new RecordingNotificationClient();
        nint pointer = Marshal.GetIUnknownForObject(client);

        int hr = ComActivation.TakeInterface<IMMNotificationClient>(0, pointer, out IMMNotificationClient? result);

        Assert.AreEqual(0, hr);
        Assert.AreSame(client, result);
    }

    [TestMethod]
    public void CreatingAnUnregisteredClassReturnsTheHresult()
    {
        int hr = 0;
        IMMDeviceEnumerator? result = null;
        MtaThread.Run(() => hr = ComActivation.Create(UnregisteredClass, ComActivation.CLSCTX_INPROC_SERVER, out result), Timeout);

        Assert.AreEqual(ComActivation.REGDB_E_CLASSNOTREG, hr, NativeCodes.Name(hr));
        Assert.AreEqual("REGDB_E_CLASSNOTREG", NativeCodes.Name(hr));
        Assert.IsNull(result);
    }

    [TestMethod]
    public void AskingAClassForAnInterfaceItLacksReturnsENoInterface()
    {
        int hr = 0;
        ITaskService? result = null;
        MtaThread.Run(() => hr = ComActivation.Create(CoreAudio.CLSID_MMDeviceEnumerator, ComActivation.CLSCTX_INPROC_SERVER, out result), Timeout);

        Assert.AreEqual(CoreAudio.E_NOINTERFACE, hr, NativeCodes.Name(hr));
        Assert.IsNull(result);
    }

    [TestMethod]
    public void CreatesTheEnumeratorAndTheTaskServiceOnAnMtaThread()
    {
        MtaThread.Run(() =>
        {
            int enumeratorHr = CoreAudio.TryCreateEnumerator(out IMMDeviceEnumerator? enumerator);
            Assert.AreEqual(0, enumeratorHr, NativeCodes.Name(enumeratorHr));
            Assert.IsNotNull(enumerator);
            Marshal.ReleaseComObject(enumerator);

            int serviceHr = TaskSchedulerCom.TryCreateService(out ITaskService? service);
            Assert.AreEqual(0, serviceHr, NativeCodes.Name(serviceHr));
            Assert.IsNotNull(service);
            Marshal.ReleaseComObject(service);
        }, Timeout);
    }

    private sealed class RecordingNotificationClient : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => 0;

        public int OnDeviceAdded(string pwstrDeviceId) => 0;

        public int OnDeviceRemoved(string pwstrDeviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? pwstrDefaultDeviceId) => 0;

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) => 0;
    }
}

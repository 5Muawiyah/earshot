using System.Runtime.InteropServices;
using System.Text;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// Hardware-free checks of the small helpers next to the declarations: PROPVARIANT reads that always clear,
// CoTaskMem strings, multi-sz and DEVPROPTYPE decoding, and Bluetooth name and address formatting.
[TestClass]
public sealed class InteropHelpersTests
{
    private static readonly Guid SampleContainer = new("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");
    private static readonly string[] ExpectedIds = ["BTHENUM\\DEV_5A6B7C8D9EAF\\X", "BTH\\MS_BTHBRB\\a&3c4d5e6&0&1"];
    private static readonly string[] Unterminated = ["unterminated"];
    private static readonly string[] TwoItems = ["a", "b"];

    // BluetoothEnumerateInstalledServices returned ERROR_MORE_DATA with the whole count for a buffer that was too
    // small (research probe). A result whose count leaves room in the buffer holds every service.
    [TestMethod]
    public void AnInstalledServiceListThatFitsIsCompleteWhateverTheCallSaid()
    {
        Guid[] eight = Enumerable.Range(1, 8).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();
        var sizes = new List<uint>();

        uint rc = BluetoothApis.GetInstalledServices((ref uint count, Guid[]? buffer) =>
        {
            sizes.Add(count);
            if (buffer is not null)
            {
                eight.AsSpan(0, Math.Min(8, (int)count)).CopyTo(buffer);
            }

            count = 8;
            return BluetoothApis.ERROR_MORE_DATA;
        }, out Guid[] services);

        Assert.AreEqual(BluetoothApis.ERROR_SUCCESS, rc, "Eight services in a buffer with room for more is the whole list.");
        CollectionAssert.AreEqual(eight, services);
        CollectionAssert.AreEqual(new uint[] { 0, 8 + BluetoothApis.ServicesSlack }, sizes);
    }

    [TestMethod]
    public void AListThatKeepsFillingTheBufferStaysIncomplete()
    {
        int calls = 0;

        uint rc = BluetoothApis.GetInstalledServices((ref uint count, Guid[]? buffer) =>
        {
            calls++;
            // Always reports exactly as many as there is room for, so no read can show the list ended.
            if (buffer is null)
            {
                count = 2;
            }

            return BluetoothApis.ERROR_MORE_DATA;
        }, out Guid[] services);

        Assert.AreEqual(BluetoothApis.ERROR_MORE_DATA, rc);
        Assert.AreEqual(5, calls, "One size query and four reads.");
        Assert.IsNotEmpty(services);
    }

    [TestMethod]
    public void AFailedListCallReturnsItsErrorAndNothing()
    {
        uint rc = BluetoothApis.GetInstalledServices((ref uint _, Guid[]? _) => 1168, out Guid[] services);

        Assert.AreEqual(1168u, rc);
        Assert.IsEmpty(services);
    }

    [TestMethod]
    public void ReadStringReturnsTheValueAndClears()
    {
        var store = new FakePropertyStore(key => StringValue("Headphones (AirPods’ Pro)"));

        PropertyRead<string> read = PropVariantInterop.ReadString(store, CoreAudio.PKEY_Device_FriendlyName);

        Assert.AreEqual(0, read.Hr);
        Assert.AreEqual(PropVariantInterop.VT_LPWSTR, read.VarType);
        Assert.IsTrue(read.HasValue);
        Assert.AreEqual("Headphones (AirPods’ Pro)", read.Value);
        Assert.AreEqual(0, read.ClearHr);
        Assert.AreEqual(CoreAudio.PKEY_Device_FriendlyName.pid, store.LastKey.pid);
    }

    [TestMethod]
    public void ReadGuidReturnsTheContainerAndClears()
    {
        var store = new FakePropertyStore(key => GuidValue(SampleContainer));

        PropertyRead<Guid> read = PropVariantInterop.ReadGuid(store, CoreAudio.PKEY_Device_ContainerId);

        Assert.AreEqual(0, read.Hr);
        Assert.AreEqual(PropVariantInterop.VT_CLSID, read.VarType);
        Assert.IsTrue(read.HasValue);
        Assert.AreEqual(SampleContainer, read.Value);
        Assert.AreEqual(0, read.ClearHr);
    }

    [TestMethod]
    public void ReadUInt32ReturnsTheValue()
    {
        var store = new FakePropertyStore(key => new PROPVARIANT { vt = PropVariantInterop.VT_UI4, ulVal = 3 });

        PropertyRead<uint> read = PropVariantInterop.ReadUInt32(store, CoreAudio.PKEY_AudioEndpoint_FormFactor);

        Assert.IsTrue(read.HasValue);
        Assert.AreEqual(3u, read.Value);
        Assert.AreEqual(0, read.ClearHr);
    }

    [TestMethod]
    public void AWrongTypeIsReportedWithoutAValueAndStillCleared()
    {
        // A VT_CLSID value read as a string: no value, the type is reported, and the GUID memory is freed.
        var store = new FakePropertyStore(key => GuidValue(SampleContainer));

        PropertyRead<string> read = PropVariantInterop.ReadString(store, CoreAudio.PKEY_Device_FriendlyName);

        Assert.AreEqual(0, read.Hr);
        Assert.AreEqual(PropVariantInterop.VT_CLSID, read.VarType);
        Assert.IsFalse(read.HasValue);
        Assert.IsNull(read.Value);
        Assert.AreEqual(0, read.ClearHr);
    }

    [TestMethod]
    public void AnAbsentPropertyIsEmpty()
    {
        var store = new FakePropertyStore(key => default);

        PropertyRead<Guid> read = PropVariantInterop.ReadGuid(store, CoreAudio.PKEY_Device_ContainerId);

        Assert.AreEqual(0, read.Hr);
        Assert.AreEqual(PropVariantInterop.VT_EMPTY, read.VarType);
        Assert.IsFalse(read.HasValue);
        Assert.AreEqual(Guid.Empty, read.Value);
    }

    [TestMethod]
    public void AFailedGetValueKeepsItsHresult()
    {
        const int noSuchDevinst = unchecked((int)0xE000020B);
        var store = new FakePropertyStore(key => default, noSuchDevinst);

        PropertyRead<string> read = PropVariantInterop.ReadString(store, CoreAudio.PKEY_Device_FriendlyName);

        Assert.AreEqual(noSuchDevinst, read.Hr);
        Assert.AreEqual("ERROR_NO_SUCH_DEVINST", Earshot.Contracts.NativeCodes.Name(read.Hr));
        Assert.IsFalse(read.HasValue);
        Assert.AreEqual(0, read.ClearHr);
    }

    [TestMethod]
    public void PropVariantClearFreesAndZeroes()
    {
        PROPVARIANT pv = StringValue("x");

        int hr = PropVariantInterop.PropVariantClear(ref pv);

        Assert.AreEqual(0, hr);
        Assert.AreEqual(PropVariantInterop.VT_EMPTY, pv.vt);
        Assert.AreEqual(0, pv.pointerValue);
    }

    [TestMethod]
    public void TakeCoTaskStringReadsAndFrees()
    {
        nint pointer = Marshal.StringToCoTaskMemUni("{2}.\\\\?\\bthenum#src");

        Assert.AreEqual("{2}.\\\\?\\bthenum#src", CoreAudio.TakeCoTaskString(pointer));
        Assert.IsNull(CoreAudio.TakeCoTaskString(0));
    }

    [TestMethod]
    public void ParseMultiSzSplitsTheList()
    {
        string[] ids = CfgMgr32.ParseMultiSz("BTHENUM\\DEV_5A6B7C8D9EAF\\X\0BTH\\MS_BTHBRB\\a&3c4d5e6&0&1\0\0".AsSpan());

        CollectionAssert.AreEqual(ExpectedIds, ids);
        Assert.IsEmpty(CfgMgr32.ParseMultiSz("\0\0".AsSpan()));
        Assert.IsEmpty(CfgMgr32.ParseMultiSz(ReadOnlySpan<char>.Empty));
        CollectionAssert.AreEqual(Unterminated, CfgMgr32.ParseMultiSz("unterminated".AsSpan()));
    }

    [TestMethod]
    public void DecodesDevPropStrings()
    {
        byte[] data = Encoding.Unicode.GetBytes("Owner’s AirPods Pro\0");

        Assert.IsTrue(CfgMgr32.TryDecodeString(CfgMgr32.DEVPROP_TYPE_STRING, data, out string? value));
        Assert.AreEqual("Owner’s AirPods Pro", value);
        Assert.IsFalse(CfgMgr32.TryDecodeString(CfgMgr32.DEVPROP_TYPE_GUID, data, out _));

        byte[] list = Encoding.Unicode.GetBytes("a\0b\0\0");
        Assert.IsTrue(CfgMgr32.TryDecodeStringList(CfgMgr32.DEVPROP_TYPE_STRING_LIST, list, out string[] items));
        CollectionAssert.AreEqual(TwoItems, items);
    }

    [TestMethod]
    public void DecodesDevPropGuidUInt32AndBoolean()
    {
        Assert.IsTrue(CfgMgr32.TryDecodeGuid(CfgMgr32.DEVPROP_TYPE_GUID, SampleContainer.ToByteArray(), out Guid container));
        Assert.AreEqual(SampleContainer, container);
        Assert.IsFalse(CfgMgr32.TryDecodeGuid(CfgMgr32.DEVPROP_TYPE_GUID, new byte[8], out _));

        Assert.IsTrue(CfgMgr32.TryDecodeUInt32(CfgMgr32.DEVPROP_TYPE_UINT32, BitConverter.GetBytes(0x0380600Au), out uint status));
        Assert.AreEqual(0x0380600Au, status);
        Assert.IsTrue(CfgMgr32.TryDecodeUInt32(CfgMgr32.DEVPROP_TYPE_INT32, BitConverter.GetBytes(1), out uint configFlags));
        Assert.AreEqual(CfgMgr32.CONFIGFLAG_DISABLED, configFlags);

        Assert.IsTrue(CfgMgr32.TryDecodeBoolean(CfgMgr32.DEVPROP_TYPE_BOOLEAN, [CfgMgr32.DEVPROP_TRUE], out bool present));
        Assert.IsTrue(present);
        Assert.IsTrue(CfgMgr32.TryDecodeBoolean(CfgMgr32.DEVPROP_TYPE_BOOLEAN, [CfgMgr32.DEVPROP_FALSE], out present));
        Assert.IsFalse(present);
        Assert.IsFalse(CfgMgr32.TryDecodeBoolean(CfgMgr32.DEVPROP_TYPE_UINT32, [0xFF], out _));
    }

    [TestMethod]
    public void FormatsTheAddressAsTwelveHexDigits()
    {
        Assert.AreEqual("5A6B7C8D9EAF", BluetoothApis.FormatAddress12(0x5A6B7C8D9EAFUL));
        Assert.AreEqual("000000000001", BluetoothApis.FormatAddress12(1));
        Assert.AreEqual("FFFFFFFFFFFF", BluetoothApis.FormatAddress12(ulong.MaxValue));
    }

    [TestMethod]
    public void NewDeviceInfoCarriesItsSizeAndNamesReadToTheFirstNul()
    {
        BLUETOOTH_DEVICE_INFO info = BluetoothApis.NewDeviceInfo();

        Assert.AreEqual(560u, info.dwSize);
        Assert.AreEqual(string.Empty, BluetoothApis.GetName(info));
    }

    private static PROPVARIANT StringValue(string text) =>
        new() { vt = PropVariantInterop.VT_LPWSTR, pointerValue = Marshal.StringToCoTaskMemUni(text) };

    private static PROPVARIANT GuidValue(Guid value)
    {
        nint pointer = Marshal.AllocCoTaskMem(16);
        Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
        return new PROPVARIANT { vt = PropVariantInterop.VT_CLSID, pointerValue = pointer };
    }

    // Called directly, not through COM: GetValue hands out CoTaskMem memory exactly as a real store does,
    // and the helper under test must free it with PropVariantClear.
    private sealed class FakePropertyStore(Func<PROPERTYKEY, PROPVARIANT> value, int hr = 0) : IPropertyStore
    {
        public PROPERTYKEY LastKey { get; private set; }

        public int GetCount(out uint cProps)
        {
            cProps = 1;
            return 0;
        }

        public int GetAt(uint iProp, out PROPERTYKEY pkey)
        {
            pkey = default;
            return unchecked((int)0x80004001);
        }

        public int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv)
        {
            LastKey = key;
            pv = hr >= 0 ? value(key) : default;
            return hr;
        }

        public int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar) => unchecked((int)0x80004001);

        public int Commit() => unchecked((int)0x80004001);
    }
}

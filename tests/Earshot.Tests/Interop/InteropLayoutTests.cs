using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KSPROPERTY = Earshot.Interop.KSIDENTIFIER;

namespace Earshot.Tests.Interop;

// Struct sizes and field offsets against the x64 layouts verified in the research probe and the SDK headers.
[TestClass]
public sealed class InteropLayoutTests
{
    private static int Offset<T>(string field) => Marshal.OffsetOf<T>(field).ToInt32();

    [TestMethod]
    public void AssertSizesPassesOnThisProcess()
    {
        InteropLayout.AssertSizes();
    }

    [TestMethod]
    public void ExpectThrowsAClearMessageOnMismatch()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => InteropLayout.Expect("PROPVARIANT", 16, 24));
        Assert.AreEqual("Interop layout mismatch: PROPVARIANT is 16 bytes, expected 24.", ex.Message);
    }

    [TestMethod]
    public void ByHandleFileInformationIs52BytesWithTheLinkCountAt40()
    {
        Assert.AreEqual(52, Marshal.SizeOf<BY_HANDLE_FILE_INFORMATION>());
        Assert.AreEqual(0, Offset<BY_HANDLE_FILE_INFORMATION>(nameof(BY_HANDLE_FILE_INFORMATION.FileAttributes)));
        Assert.AreEqual(40, Offset<BY_HANDLE_FILE_INFORMATION>(nameof(BY_HANDLE_FILE_INFORMATION.NumberOfLinks)));
    }

    [TestMethod]
    public void ProcessIsSixtyFourBit()
    {
        Assert.AreEqual(8, IntPtr.Size);
    }

    [TestMethod]
    public void PropVariantIs24BytesWithTheValueAtOffset8()
    {
        Assert.AreEqual(24, Marshal.SizeOf<PROPVARIANT>());
        Assert.AreEqual(24, Unsafe.SizeOf<PROPVARIANT>());
        Assert.AreEqual(0, Offset<PROPVARIANT>(nameof(PROPVARIANT.vt)));
        Assert.AreEqual(2, Offset<PROPVARIANT>(nameof(PROPVARIANT.wReserved1)));
        Assert.AreEqual(4, Offset<PROPVARIANT>(nameof(PROPVARIANT.wReserved2)));
        Assert.AreEqual(6, Offset<PROPVARIANT>(nameof(PROPVARIANT.wReserved3)));
        Assert.AreEqual(8, Offset<PROPVARIANT>(nameof(PROPVARIANT.pointerValue)));
        Assert.AreEqual(8, Offset<PROPVARIANT>(nameof(PROPVARIANT.ulVal)));
        Assert.AreEqual(8, Offset<PROPVARIANT>(nameof(PROPVARIANT.boolVal)));
        Assert.AreEqual(8, Offset<PROPVARIANT>(nameof(PROPVARIANT.blob)));
        Assert.AreEqual(16, Marshal.SizeOf<BLOB>());
    }

    [TestMethod]
    public void PropertyKeysAre20Bytes()
    {
        Assert.AreEqual(20, Marshal.SizeOf<PROPERTYKEY>());
        Assert.AreEqual(16, Offset<PROPERTYKEY>(nameof(PROPERTYKEY.pid)));
        Assert.AreEqual(20, Marshal.SizeOf<DEVPROPKEY>());
        Assert.AreEqual(20, Unsafe.SizeOf<DEVPROPKEY>());
        Assert.AreEqual(16, Offset<DEVPROPKEY>(nameof(DEVPROPKEY.pid)));
    }

    [TestMethod]
    public void KsIdentifierIs24BytesAndEightByteAligned()
    {
        Assert.AreEqual(24, Marshal.SizeOf<KSIDENTIFIER>());
        Assert.AreEqual(24, Unsafe.SizeOf<KSIDENTIFIER>());
        Assert.AreEqual(0, Offset<KSIDENTIFIER>(nameof(KSIDENTIFIER.Set)));
        Assert.AreEqual(16, Offset<KSIDENTIFIER>(nameof(KSIDENTIFIER.Id)));
        Assert.AreEqual(20, Offset<KSIDENTIFIER>(nameof(KSIDENTIFIER.Flags)));
        Assert.AreEqual(0, Offset<KSIDENTIFIER>(nameof(KSIDENTIFIER.Alignment)));
    }

    [TestMethod]
    public void KspPinIs32BytesAndStartsWithItsProperty()
    {
        Assert.AreEqual(32, Marshal.SizeOf<KSP_PIN>());
        Assert.AreEqual(32, Unsafe.SizeOf<KSP_PIN>());
        Assert.AreEqual(0, Offset<KSP_PIN>(nameof(KSP_PIN.Property)));
        Assert.AreEqual(24, Offset<KSP_PIN>(nameof(KSP_PIN.PinId)));
        Assert.AreEqual(28, Offset<KSP_PIN>(nameof(KSP_PIN.Reserved)));
    }

    [TestMethod]
    public void AsPropertyAliasesTheStartOfTheKspPin()
    {
        var pin = new KSP_PIN(new KSPROPERTY(KsControl.KSPROPSETID_Pin, KsControl.KSPROPERTY_PIN_DATAFLOW, KsControl.KSPROPERTY_TYPE_GET), 3);
        ref KSPROPERTY property = ref KsControl.AsProperty(ref pin);

        Assert.IsTrue(Unsafe.AreSame(ref property, ref pin.Property));
        Assert.AreEqual(KsControl.KSPROPERTY_PIN_DATAFLOW, property.Id);
        Assert.AreEqual(3u, pin.PinId);
    }

    [TestMethod]
    public void KsJackDescriptionIs28Bytes()
    {
        Assert.AreEqual(28, Marshal.SizeOf<KSJACK_DESCRIPTION>());
        Assert.AreEqual(24, Offset<KSJACK_DESCRIPTION>(nameof(KSJACK_DESCRIPTION.IsConnected)));
    }

    [TestMethod]
    public void BluetoothDeviceInfoIs560BytesWithVerifiedOffsets()
    {
        Assert.AreEqual(560, Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>());
        Assert.AreEqual(560, Unsafe.SizeOf<BLUETOOTH_DEVICE_INFO>());
        Assert.AreEqual(0, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.dwSize)));
        Assert.AreEqual(8, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.Address)));
        Assert.AreEqual(16, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.ulClassofDevice)));
        Assert.AreEqual(20, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.fConnected)));
        Assert.AreEqual(24, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.fRemembered)));
        Assert.AreEqual(28, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.fAuthenticated)));
        Assert.AreEqual(32, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.stLastSeen)));
        Assert.AreEqual(48, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.stLastUsed)));
        Assert.AreEqual(64, Offset<BLUETOOTH_DEVICE_INFO>(nameof(BLUETOOTH_DEVICE_INFO.szName)));
        Assert.AreEqual(16, Marshal.SizeOf<SYSTEMTIME>());
    }

    [TestMethod]
    public void BluetoothSearchParamsAre40BytesWithThePaddedRadioHandle()
    {
        Assert.AreEqual(40, Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>());
        Assert.AreEqual(40, Unsafe.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>());
        Assert.AreEqual(20, Offset<BLUETOOTH_DEVICE_SEARCH_PARAMS>(nameof(BLUETOOTH_DEVICE_SEARCH_PARAMS.fIssueInquiry)));
        Assert.AreEqual(24, Offset<BLUETOOTH_DEVICE_SEARCH_PARAMS>(nameof(BLUETOOTH_DEVICE_SEARCH_PARAMS.cTimeoutMultiplier)));
        Assert.AreEqual(32, Offset<BLUETOOTH_DEVICE_SEARCH_PARAMS>(nameof(BLUETOOTH_DEVICE_SEARCH_PARAMS.hRadio)));
        Assert.AreEqual(4, Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>());
    }

    [TestMethod]
    public void AppBarDataIs48BytesWithTheRectAtOffset24()
    {
        Assert.AreEqual(48, Marshal.SizeOf<APPBARDATA>());
        Assert.AreEqual(8, Offset<APPBARDATA>(nameof(APPBARDATA.hWnd)));
        Assert.AreEqual(20, Offset<APPBARDATA>(nameof(APPBARDATA.uEdge)));
        Assert.AreEqual(24, Offset<APPBARDATA>(nameof(APPBARDATA.rc)));
        Assert.AreEqual(40, Offset<APPBARDATA>(nameof(APPBARDATA.lParam)));
        Assert.AreEqual(16, Marshal.SizeOf<RECT>());
        Assert.AreEqual(8, Marshal.SizeOf<POINT>());
        Assert.AreEqual(8, Marshal.SizeOf<SIZE>());
    }
}

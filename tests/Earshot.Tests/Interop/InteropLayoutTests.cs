using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KSPROPERTY = Earshot.Interop.KSIDENTIFIER;

namespace Earshot.Tests.Interop;

// Struct sizes and field offsets against the x64 layouts in the SDK headers, as measured on this machine.
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

    // devpropdef.h, devfiltertypes.h and devquerydef.h on x64: pointers are 8 bytes and align to 8.
    [TestMethod]
    public void TheDeviceQueryStructsHaveTheirX64Layout()
    {
        Assert.AreEqual(32, Marshal.SizeOf<DEVPROPCOMPKEY>());
        Assert.AreEqual(20, Offset<DEVPROPCOMPKEY>(nameof(DEVPROPCOMPKEY.Store)));
        Assert.AreEqual(24, Offset<DEVPROPCOMPKEY>(nameof(DEVPROPCOMPKEY.LocaleName)));

        Assert.AreEqual(48, Marshal.SizeOf<DEVPROPERTY>());
        Assert.AreEqual(32, Offset<DEVPROPERTY>(nameof(DEVPROPERTY.Type)));
        Assert.AreEqual(36, Offset<DEVPROPERTY>(nameof(DEVPROPERTY.BufferSize)));
        Assert.AreEqual(40, Offset<DEVPROPERTY>(nameof(DEVPROPERTY.Buffer)));

        Assert.AreEqual(56, Marshal.SizeOf<DEVPROP_FILTER_EXPRESSION>());
        Assert.AreEqual(8, Offset<DEVPROP_FILTER_EXPRESSION>(nameof(DEVPROP_FILTER_EXPRESSION.Property)));

        Assert.AreEqual(32, Unsafe.SizeOf<DEV_OBJECT>());
        Assert.AreEqual(8, Offset<DEV_OBJECT>(nameof(DEV_OBJECT.pszObjectId)));
        Assert.AreEqual(16, Offset<DEV_OBJECT>(nameof(DEV_OBJECT.cPropertyCount)));
        Assert.AreEqual(24, Offset<DEV_OBJECT>(nameof(DEV_OBJECT.pProperties)));
    }

    [TestMethod]
    public void TheDeviceQueryKeysAreTheSdkOnes()
    {
        Assert.AreEqual(new Guid("A35996AB-11CF-4935-8B61-A6761081ECDF"), DevQuery.PKEY_Devices_Aep_IsPaired.fmtid);
        Assert.AreEqual(16u, DevQuery.PKEY_Devices_Aep_IsPaired.pid);
        Assert.AreEqual(new Guid("0BBA1EDE-7566-4F47-90EC-25FC567CED2A"), DevQuery.PKEY_Devices_AepContainer_IsPaired.fmtid);
        Assert.AreEqual(4u, DevQuery.PKEY_Devices_AepContainer_IsPaired.pid);
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

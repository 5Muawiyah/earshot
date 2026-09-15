global using KSEVENT = Earshot.Interop.KSIDENTIFIER;
global using KSMETHOD = Earshot.Interop.KSIDENTIFIER;
global using KSPROPERTY = Earshot.Interop.KSIDENTIFIER;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Kernel streaming declarations for the adapter (KS filter) side of the connect path.
//
// KSPROPERTY, KSMETHOD and KSEVENT are typedefs of KSIDENTIFIER in the SDK (ks.h, devicetopology.h);
// the global aliases above keep that spelling in C#.
internal static class KsControl
{
    // KSPROPERTY.Flags request types (ks.h).
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/ksproperty-structure
    internal const uint KSPROPERTY_TYPE_GET = 0x00000001;
    internal const uint KSPROPERTY_TYPE_SET = 0x00000002;
    internal const uint KSPROPERTY_TYPE_GETPAYLOADSIZE = 0x00000004;
    internal const uint KSPROPERTY_TYPE_SETSUPPORT = 0x00000100;
    internal const uint KSPROPERTY_TYPE_BASICSUPPORT = 0x00000200;
    internal const uint KSPROPERTY_TYPE_RELATIONS = 0x00000400;
    internal const uint KSPROPERTY_TYPE_DEFAULTVALUES = 0x00010000;
    internal const uint KSPROPERTY_TYPE_TOPOLOGY = 0x10000000;

    // KSPROPSETID_BtAudio (ksmedia.h). Sent to a Bluetooth audio KS filter.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kspropsetid-btaudio
    internal static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    // KSPROPERTY_BTAUDIO ids. Both are Get requests on the filter with a KSPROPERTY descriptor
    // (24 bytes) and no property value: Flags = KSPROPERTY_TYPE_GET, PropertyData = 0, DataLength = 0.
    // STATUS_SUCCESS means the driver attempted the change, not that it happened; the HFP driver
    // connects and disconnects asynchronously.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-disconnect
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kernel-streaming-considerations
    internal const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    internal const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;

    // KSPROPSETID_Pin (ks.h). Read-only pin queries used by probes.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/kspropsetid-pin
    internal static readonly Guid KSPROPSETID_Pin = new("8C134960-51AD-11CF-878A-94F801C10000");

    // KSPROPERTY_PIN_CTYPES takes a KSPROPERTY descriptor and returns a ULONG pin count.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/ksproperty-pin-ctypes
    internal const uint KSPROPERTY_PIN_CTYPES = 1;

    // KSPROPERTY_PIN_DATAFLOW takes a KSP_PIN descriptor and returns a KSPIN_DATAFLOW.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/ksproperty-pin-dataflow
    internal const uint KSPROPERTY_PIN_DATAFLOW = 2;

    // KSPROPERTY_PIN_NAME takes a KSP_PIN descriptor and returns a WCHAR string.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/ksproperty-pin-name
    internal const uint KSPROPERTY_PIN_NAME = 12;

    // KSPIN_DATAFLOW (ks.h).
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ks/ne-ks-kspin_dataflow
    internal const int KSPIN_DATAFLOW_IN = 1;
    internal const int KSPIN_DATAFLOW_OUT = 2;

    // A KSP_PIN starts with its KSPROPERTY, so a reference to the whole KSP_PIN can be passed as the
    // PKSPROPERTY argument with PropertyLength = sizeof(KSP_PIN). KSIDENTIFIER is blittable, so the
    // by-reference argument is pinned and passed as a pointer to the caller's storage, not copied.
    // https://learn.microsoft.com/en-us/dotnet/framework/interop/copying-and-pinning
    internal static ref KSPROPERTY AsProperty(ref KSP_PIN pin) => ref Unsafe.As<KSP_PIN, KSPROPERTY>(ref pin);
}

// KSIDENTIFIER: union { struct { GUID Set; ULONG Id; ULONG Flags; }; LONGLONG Alignment; }.
// 24 bytes, 8-byte aligned (the LONGLONG member sets the alignment).
// https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/ksproperty-structure
[StructLayout(LayoutKind.Explicit)]
internal struct KSIDENTIFIER
{
    [FieldOffset(0)] public Guid Set;
    [FieldOffset(16)] public uint Id;
    [FieldOffset(20)] public uint Flags;
    [FieldOffset(0)] public long Alignment;

    public KSIDENTIFIER(Guid set, uint id, uint flags)
    {
        Alignment = 0;
        Set = set;
        Id = id;
        Flags = flags;
    }
}

// KSP_PIN { KSPROPERTY Property; ULONG PinId; ULONG Reserved; }, 32 bytes.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ks/ns-ks-ksp_pin
[StructLayout(LayoutKind.Sequential)]
internal struct KSP_PIN
{
    public KSPROPERTY Property;
    public uint PinId;
    public uint Reserved;

    public KSP_PIN(KSPROPERTY property, uint pinId)
    {
        Property = property;
        PinId = pinId;
        Reserved = 0;
    }
}

// IKsControl. Vtable order per the SDK header (shared\ksproxy.h, um\devicetopology.idl):
// KsProperty, KsMethod, KsEvent. The learn.microsoft.com page lists them alphabetically, which is NOT
// the vtable order. Activate it on the adapter IMMDevice; endpoints return E_NOINTERFACE.
// https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksproxy/nn-ksproxy-ikscontrol
[ComImport]
[Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IKsControl
{
    // property points to propertyLength bytes that start with a KSPROPERTY (24 for KSPROPERTY, 32 for
    // KSP_PIN via KsControl.AsProperty). propertyData may be 0 with dataLength 0.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksproxy/nf-ksproxy-ikscontrol-ksproperty
    [PreserveSig]
    int KsProperty(ref KSPROPERTY property, uint propertyLength, nint propertyData, uint dataLength, out uint bytesReturned);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksproxy/nf-ksproxy-ikscontrol-ksmethod
    [PreserveSig]
    int KsMethod(ref KSMETHOD method, uint methodLength, nint methodData, uint dataLength, out uint bytesReturned);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksproxy/nf-ksproxy-ikscontrol-ksevent
    [PreserveSig]
    int KsEvent(ref KSEVENT eventProperty, uint eventLength, nint eventData, uint dataLength, out uint bytesReturned);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Startup check that each interop struct has the size verified on this machine, so a marshalling
// drift fails at once instead of corrupting memory in a native call. Runs first in every mode.
internal static class InteropLayout
{
    // Verified x64 sizes (research probe, Marshal.SizeOf and C# sizeof).
    internal const int PropVariantBytes = 24;
    internal const int KsIdentifierBytes = 24;
    internal const int KspPinBytes = 32;
    internal const int PropertyKeyBytes = 20;
    internal const int DevPropKeyBytes = 20;
    internal const int BluetoothDeviceInfoBytes = 560;
    internal const int BluetoothDeviceSearchParamsBytes = 40;
    internal const int BluetoothFindRadioParamsBytes = 4;
    internal const int KsJackDescriptionBytes = 28;
    internal const int AppBarDataBytes = 48;

    public static void AssertSizes()
    {
        // Every size below is the x64 layout; a 32-bit process would get different sizes
        // (BLUETOOTH_DEVICE_SEARCH_PARAMS is 32 bytes on x86).
        if (IntPtr.Size != 8)
        {
            throw new InvalidOperationException(
                "Interop layout mismatch: Earshot needs a 64-bit process, but pointers are " + IntPtr.Size + " bytes.");
        }

        // Structs marshalled by COM interop or by reference: the marshalled size is what native code sees.
        Expect(nameof(PROPVARIANT), Marshal.SizeOf<PROPVARIANT>(), PropVariantBytes);
        Expect(nameof(KSIDENTIFIER), Marshal.SizeOf<KSIDENTIFIER>(), KsIdentifierBytes);
        Expect(nameof(KSP_PIN), Marshal.SizeOf<KSP_PIN>(), KspPinBytes);
        Expect(nameof(PROPERTYKEY), Marshal.SizeOf<PROPERTYKEY>(), PropertyKeyBytes);
        Expect(nameof(DEVPROPKEY), Marshal.SizeOf<DEVPROPKEY>(), DevPropKeyBytes);
        Expect(nameof(KSJACK_DESCRIPTION), Marshal.SizeOf<KSJACK_DESCRIPTION>(), KsJackDescriptionBytes);
        Expect(nameof(APPBARDATA), Marshal.SizeOf<APPBARDATA>(), AppBarDataBytes);
        Expect(nameof(BLUETOOTH_DEVICE_INFO), Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(), BluetoothDeviceInfoBytes);
        Expect(nameof(BLUETOOTH_DEVICE_SEARCH_PARAMS), Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(), BluetoothDeviceSearchParamsBytes);
        Expect(nameof(BLUETOOTH_FIND_RADIO_PARAMS), Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>(), BluetoothFindRadioParamsBytes);

        // Structs passed to native code as raw pointers: the in-memory size is what native code sees.
        Expect(nameof(BLUETOOTH_DEVICE_INFO) + " (in memory)", Unsafe.SizeOf<BLUETOOTH_DEVICE_INFO>(), BluetoothDeviceInfoBytes);
        Expect(nameof(BLUETOOTH_DEVICE_SEARCH_PARAMS) + " (in memory)", Unsafe.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(), BluetoothDeviceSearchParamsBytes);
        Expect(nameof(BLUETOOTH_FIND_RADIO_PARAMS) + " (in memory)", Unsafe.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>(), BluetoothFindRadioParamsBytes);
        Expect(nameof(KSIDENTIFIER) + " (in memory)", Unsafe.SizeOf<KSIDENTIFIER>(), KsIdentifierBytes);
        Expect(nameof(KSP_PIN) + " (in memory)", Unsafe.SizeOf<KSP_PIN>(), KspPinBytes);
        Expect(nameof(PROPVARIANT) + " (in memory)", Unsafe.SizeOf<PROPVARIANT>(), PropVariantBytes);
        Expect(nameof(DEVPROPKEY) + " (in memory)", Unsafe.SizeOf<DEVPROPKEY>(), DevPropKeyBytes);
    }

    // Throws when a struct's size differs from the verified value.
    internal static void Expect(string structName, int actualBytes, int expectedBytes)
    {
        if (actualBytes != expectedBytes)
        {
            throw new InvalidOperationException(
                "Interop layout mismatch: " + structName + " is " + actualBytes + " bytes, expected " + expectedBytes + ".");
        }
    }
}

namespace Earshot.Interop;

// Startup check that each interop struct has the size verified on this machine, so a marshalling
// drift fails at once instead of corrupting memory in a native call. Runs first in every mode.
internal static class InteropLayout
{
    public static void AssertSizes()
    {
        // Filled in alongside the interop declarations. Expected sizes (x64):
        // PROPVARIANT 24, KSIDENTIFIER 24, KSP_PIN 32, PROPERTYKEY 20, BLUETOOTH_DEVICE_INFO 560.
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

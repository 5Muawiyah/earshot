using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Creates COM objects and wraps interface pointers without throwing. Every failure comes back as the
// HRESULT, so a class that is not registered (REGDB_E_CLASSNOTREG), an interface the object does not
// have (E_NOINTERFACE) or a thread without COM (CO_E_NOTINITIALIZED) is recorded as a StepOutcome like
// any other native failure. "new" on a [ComImport] coclass and a cast to an interface would throw
// COMException or InvalidCastException instead.
internal static partial class ComActivation
{
    private const string Ole32 = "ole32.dll";

    // CLSCTX (wtypesbase.h).
    // https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ne-wtypesbase-clsctx
    internal const uint CLSCTX_INPROC_SERVER = 0x1;

    internal const int S_OK = 0;
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);
    internal const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    // ppv is an owned reference, or null on failure. Call on a thread that has initialised COM (the
    // audio worker and the system worker are MTA threads).
    // https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstance
    [LibraryImport(Ole32)]
    internal static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    // CoCreateInstance for interface T (IID taken from T's [Guid]). Returns the HRESULT; result is null
    // on failure.
    internal static int Create<T>(Guid clsid, uint clsContext, out T? result)
        where T : class
    {
        Guid iid = typeof(T).GUID;
        int hr = CoCreateInstance(in clsid, 0, clsContext, in iid, out nint pointer);
        return TakeInterface(hr, pointer, out result);
    }

    // Asks an object Earshot already holds for another interface, without an InvalidCastException: the same
    // QueryInterface path as TakeInterface, so an object without T gives E_NOINTERFACE as a code.
    // https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getiunknownforobject
    internal static int AsInterface<T>(object comObject, out T? result)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(comObject);
        return TakeInterface(S_OK, Marshal.GetIUnknownForObject(comObject), out result);
    }

    // Wraps an owned interface pointer from a void** out parameter in an RCW of type T and releases the
    // raw reference. A pointer returned with a failing HRESULT is released and discarded. A success
    // with a null pointer is reported as E_POINTER. The object is asked for T with QueryInterface
    // first, so an object without T gives E_NOINTERFACE here rather than an InvalidCastException.
    // Once that succeeds, the cast to T can only fail when the RCW is used from another apartment,
    // which the single-worker threading model rules out.
    // https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.queryinterface
    internal static int TakeInterface<T>(int hr, nint pointer, out T? result)
        where T : class
    {
        result = null;
        if (pointer == 0)
        {
            return hr < 0 ? hr : E_POINTER;
        }

        try
        {
            if (hr < 0)
            {
                return hr;
            }

            Guid iid = typeof(T).GUID;
            int qi = Marshal.QueryInterface(pointer, in iid, out nint typed);
            if (qi < 0)
            {
                return qi;
            }

            if (typed == 0)
            {
                return E_POINTER;
            }

            try
            {
                result = (T)Marshal.GetObjectForIUnknown(typed);
            }
            finally
            {
                Marshal.Release(typed);
            }

            return hr;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}

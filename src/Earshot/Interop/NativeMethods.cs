using System.Runtime.InteropServices;

// Every P/Invoke in this assembly resolves its DLL from System32 only, never from the
// application folder or the current directory.
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.defaultdllimportsearchpathsattribute
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Earshot.Interop;

// kernel32 calls made before any run mode starts.
internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    // SetDefaultDllDirectories flags.
    // https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-setdefaultdlldirectories
    internal const uint LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
    internal const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    // AttachConsole: use the console of the parent process. (DWORD)-1.
    // https://learn.microsoft.com/en-us/windows/console/attachconsole
    internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // AttachConsole fails with this when the process already has a console.
    internal const int ERROR_ACCESS_DENIED = 5;

    // Removes the current directory and PATH from the DLL search order for every later load.
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint processId);
}

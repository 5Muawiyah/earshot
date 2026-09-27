using System.Runtime.InteropServices;
using System.Text;

namespace Earshot.Interop;

// IShellLinkW and IPersistFile, for writing the per-user Start menu shortcut the toast path's
// AppUserModelID needs. Vtable order is the SDK header order (um\ShObjIdl_core.idl, um\objidl.idl), not the
// alphabetical order the learn.microsoft.com pages use. Every method keeps [PreserveSig] so a failure is a
// StepOutcome, never a thrown COMException, the same rule Interop\CoreAudio.cs follows.
// https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw
// https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-ipersistfile
internal static class ShellLinkCom
{
    // CLSID_ShellLink.
    internal static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    internal const int MAX_PATH = 260;

    // STGM_READWRITE, for IPersistFile.Save on a new file.
    // https://learn.microsoft.com/en-us/windows/win32/api/objbase/ne-objbase-stgm
    internal const uint STGM_READWRITE = 0x00000002;

    // PKEY_AppUserModel_ID, VT_LPWSTR, read from the property page rather than from memory.
    // https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-id
    internal static readonly PROPERTYKEY PKEY_AppUserModel_ID =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    [PreserveSig]
    int GetPath([Out] StringBuilder pszFile, int cchMaxPath, nint pfd, uint fFlags);

    [PreserveSig]
    int GetIDList(out nint ppidl);

    [PreserveSig]
    int SetIDList(nint pidl);

    [PreserveSig]
    int GetDescription([Out] StringBuilder pszName, int cchMaxName);

    [PreserveSig]
    int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

    [PreserveSig]
    int GetWorkingDirectory([Out] StringBuilder pszDir, int cchMaxPath);

    [PreserveSig]
    int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

    [PreserveSig]
    int GetArguments([Out] StringBuilder pszArgs, int cchMaxPath);

    [PreserveSig]
    int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

    [PreserveSig]
    int GetHotkey(out short pwHotkey);

    [PreserveSig]
    int SetHotkey(short wHotkey);

    [PreserveSig]
    int GetShowCmd(out int piShowCmd);

    [PreserveSig]
    int SetShowCmd(int iShowCmd);

    [PreserveSig]
    int GetIconLocation([Out] StringBuilder pszIconPath, int cchIconPath, out int piIcon);

    [PreserveSig]
    int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

    [PreserveSig]
    int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

    [PreserveSig]
    int Resolve(nint hwnd, uint fFlags);

    [PreserveSig]
    int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

// https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-ipersistfile
[ComImport]
[Guid("0000010B-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFile
{
    [PreserveSig]
    int GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

    [PreserveSig]
    int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    [PreserveSig]
    int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);

    [PreserveSig]
    int GetCurFile(out nint ppszFileName);
}

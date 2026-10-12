using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The lock state of the current session, read only: WTSQuerySessionInformation with WTSSessionInfoEx returns a WTSINFOEXW (a DWORD
// Level, then the WTSINFOEX_LEVEL_W union, whose one member is the level 1 structure), freed with WTSFreeMemory. The structures are
// declared as the SDK header declares them and read with PtrToStructure, so the layout is the marshaller's and not a hand count:
// the level 1 structure holds 8-byte LARGE_INTEGER members, so the union starts at offset 8 and SessionFlags is at 16.
// SessionFlags is WTS_SESSIONSTATE_LOCK 0, WTS_SESSIONSTATE_UNLOCK 1 or WTS_SESSIONSTATE_UNKNOWN 0xFFFFFFFF; Windows 7 and Server
// 2008 R2 alone have the first two reversed, and the target framework does not run there.
// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationw
// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoexw
// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w
internal static class WtsSession
{
    private const uint CurrentSession = 0xFFFFFFFF;
    private const int SessionInfoEx = 25;
    private const int LockFlag = 0;
    private const int UnlockFlag = 1;

    // WINSTATIONNAME_LENGTH 32, USERNAME_LENGTH 20 and DOMAIN_LENGTH 17 in wtsapi32.h, each plus the terminating null.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WtsInfoExLevel1
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)]
        public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)]
        public string DomainName;
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WtsInfoEx
    {
        public uint Level;
        public WtsInfoExLevel1 Data;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(nint memory);

    // What one read said. Code is the raw Win32 error when the call failed (0 otherwise); Step names the call or check that
    // failed, empty when the read worked. Flags is SessionFlags as read.
    internal sealed record Reading(bool? Locked, int Flags, uint Code, string Step);

    internal static Reading Read()
    {
        if (!WTSQuerySessionInformationW(0, CurrentSession, SessionInfoEx, out nint buffer, out uint bytes))
        {
            return new Reading(null, 0, (uint)Marshal.GetLastWin32Error(), "WTSQuerySessionInformation");
        }

        try
        {
            if (bytes < Marshal.SizeOf<WtsInfoEx>())
            {
                return new Reading(null, 0, bytes, "WTSQuerySessionInformation returned fewer bytes than WTSINFOEXW (the count is the code)");
            }

            WtsInfoEx info = Marshal.PtrToStructure<WtsInfoEx>(buffer);
            if (info.Level != 1)
            {
                return new Reading(null, 0, info.Level, "WTSINFOEXW level (the code is the level, 1 expected)");
            }

            return info.Data.SessionFlags switch
            {
                LockFlag => new Reading(true, info.Data.SessionFlags, 0, ""),
                UnlockFlag => new Reading(false, info.Data.SessionFlags, 0, ""),
                _ => new Reading(null, info.Data.SessionFlags, 0, "SessionFlags is not a documented lock value"),
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}

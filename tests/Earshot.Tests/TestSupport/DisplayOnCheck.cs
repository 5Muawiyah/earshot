using System.Runtime.InteropServices;

namespace Earshot.Tests;

// Time since the last keyboard or mouse input in this session, read only. GetLastInputInfo counts input for the whole session, so
// it needs no window and works from a test thread. https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo
internal static class InputIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    // Null when Windows gives no input time. The input time and the tick count are both milliseconds since boot, wrapping at 2^32.
    internal static TimeSpan? Read()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        uint sinceMs = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(sinceMs);
    }
}

// What reading the display-off timeout gave. Timeout is null when it could not be read, zero when the setting is "never", else the
// time. Code is the raw Win32 return of the call that failed (0 when none did); Step names that call, and Source is AC or DC.
internal readonly record struct DisplayOffTimeoutReading(TimeSpan? Timeout, uint Code, string Step, string Source)
{
    // Nothing was asked of Windows (no display to check), as opposed to a call that failed: no code, and the reason in Step.
    internal static DisplayOffTimeoutReading NotRead(string reason) => new(null, 0, "not read: " + reason, "");
}

// The active power scheme's "Turn off display after" setting, read only: PowerGetActiveScheme for the scheme, then
// PowerReadACValueIndex or PowerReadDCValueIndex for the power source in use (GetSystemPowerStatus: ACLineStatus 1 is online, 0 offline,
// 255 unknown), in the display subgroup (GUID_VIDEO_SUBGROUP 7516b95f-f776-4464-8c53-06167f40cc99, listed on
// https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex). The setting is
// GUID_VIDEO_POWERDOWN_TIMEOUT {3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e}: winnt.h in the Windows SDK (um\winnt.h, 10.0.26100.0) says it
// "Specifies (in seconds) how long we wait after the last user input has been received before we power off the video". No Microsoft web
// page found lists the GUID. That 0 means never is not in winnt.h; it is taken from powercfg and Settings (not checked in a header)
// (`powercfg /q SCHEME_CURRENT SUB_VIDEO VIDEOIDLE` here: alias VIDEOIDLE, units seconds, AC index 0x00000e10). The scheme's GUID is freed
// with LocalFree
// (https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme).
internal static class DisplayOffTimeout
{
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoPowerdownTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint dcValueIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);

    internal static DisplayOffTimeoutReading Read()
    {
        uint code = PowerGetActiveScheme(0, out nint schemePointer);
        if (code != 0)
        {
            return new DisplayOffTimeoutReading(null, code, "PowerGetActiveScheme", "");
        }

        try
        {
            if (!GetSystemPowerStatus(out SystemPowerStatus status))
            {
                return new DisplayOffTimeoutReading(null, (uint)Marshal.GetLastWin32Error(), "GetSystemPowerStatus", "");
            }

            if (status.AcLineStatus is not (0 or 1))
            {
                return new DisplayOffTimeoutReading(null, status.AcLineStatus, "GetSystemPowerStatus ACLineStatus (255 is unknown)", "");
            }

            bool onAc = status.AcLineStatus == 1;
            Guid scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            Guid subgroup = VideoSubgroup;
            Guid setting = VideoPowerdownTimeout;
            uint seconds;
            code = onAc
                ? PowerReadACValueIndex(0, ref scheme, ref subgroup, ref setting, out seconds)
                : PowerReadDCValueIndex(0, ref scheme, ref subgroup, ref setting, out seconds);
            string source = onAc ? "AC" : "DC";
            return code != 0
                ? new DisplayOffTimeoutReading(null, code, onAc ? "PowerReadACValueIndex" : "PowerReadDCValueIndex", source)
                : new DisplayOffTimeoutReading(TimeSpan.FromSeconds(seconds), 0, "", source);
        }
        finally
        {
            _ = LocalFree(schemePointer);
        }
    }
}

// What reading the session's lock state gave. Locked is null when it could not be read or the session says it does not know. Flags is
// the raw SessionFlags, Code the raw Win32 error when the call failed (0 when it did not), Step what failed.
internal readonly record struct SessionLockReading(bool? Locked, uint Flags, uint Code, string Step)
{
    // Nothing was asked of Windows (no display to check), as opposed to a call that failed: no code, and the reason in Step.
    internal static SessionLockReading NotRead(string reason) => new(null, 0, 0, "not read: " + reason);
}

// Whether this session is locked, read only: WTSQuerySessionInformation with WTSSessionInfoEx for the current session returns a WTSINFOEXW
// (a DWORD Level, then the level 1 data: SessionId, SessionState, SessionFlags, so SessionFlags is at offset 12), freed with WTSFreeMemory.
// SessionFlags is WTS_SESSIONSTATE_LOCK 0, WTS_SESSIONSTATE_UNLOCK 1 or WTS_SESSIONSTATE_UNKNOWN 0xFFFFFFFF; on Windows 7 and Server 2008 R2
// alone Microsoft documents the first two as reversed, and this reads them as documented for every later Windows, which is all the
// project's target framework runs on.
// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationw
// https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w
// A locked console has a display-off timeout of its own (GUID_VIDEO_CONSOLE_LOCK_TIMEOUT in winnt.h), so a locked PC says nothing
// about the timeout read above.
internal static class SessionLock
{
    private const uint CurrentSession = 0xFFFFFFFF;
    private const int SessionInfoEx = 25;
    private const int LevelOffset = 0;
    private const int SessionFlagsOffset = 12;
    private const uint SessionStateUnknown = 0xFFFFFFFF;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(nint memory);

    internal static SessionLockReading Read()
    {
        if (!WTSQuerySessionInformationW(0, CurrentSession, SessionInfoEx, out nint buffer, out uint bytes))
        {
            return new SessionLockReading(null, 0, (uint)Marshal.GetLastWin32Error(), "WTSQuerySessionInformation");
        }

        try
        {
            if (bytes < SessionFlagsOffset + sizeof(uint))
            {
                return new SessionLockReading(null, 0, bytes, "WTSQuerySessionInformation returned too few bytes (the count is the code)");
            }

            int level = Marshal.ReadInt32(buffer, LevelOffset);
            if (level != 1)
            {
                return new SessionLockReading(null, 0, (uint)level, "WTSINFOEXW level (the code is the level, 1 expected)");
            }

            uint flags = unchecked((uint)Marshal.ReadInt32(buffer, SessionFlagsOffset));
            return flags switch
            {
                0 => new SessionLockReading(true, flags, 0, ""),
                1 => new SessionLockReading(false, flags, 0, ""),
                SessionStateUnknown => new SessionLockReading(null, flags, 0, "SessionFlags is WTS_SESSIONSTATE_UNKNOWN"),
                _ => new SessionLockReading(null, flags, 0, "SessionFlags is not a documented value"),
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}

// What the machine says about its display before the clock is asked anything: the lock state, the display-off timeout and the time
// since input. Read only on a local session; elsewhere (a hosted runner, a remote session, a machine with no screen) there is no
// display to check and nothing is read.
internal sealed record DisplayEvidence(SessionLockReading Lock, DisplayOffTimeoutReading Timeout, TimeSpan? IdleBefore)
{
    internal static DisplayEvidence Read(bool localSession) =>
        localSession
            ? new DisplayEvidence(SessionLock.Read(), DisplayOffTimeout.Read(), InputIdle.Read())
            : new DisplayEvidence(SessionLockReading.NotRead(NotLocal), DisplayOffTimeoutReading.NotRead(NotLocal), null);

    private const string NotLocal = "not a local session, so there is no display to check";
}

internal sealed record DisplayVerdict(bool KnownOn, string Branch, string Reason);

// Whether the display is known to be on, from the display-off timeout and the time since input, both read without the clock under
// test. Known on means the timeout has not run out since the last input, with a margin so a run ending near the boundary is not
// misjudged; a timeout of never is always on. Anything else (a locked session, whose console has a timeout of its own, or a lock
// state or timeout that could not be read, or a timeout that has run out) is unknown, and only then may the clock's own account of
// an unpaced run be believed.
internal static class DisplayState
{
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    internal static DisplayVerdict Decide(bool localSession, SessionLockReading sessionLock, DisplayOffTimeoutReading timeout, TimeSpan? idleBefore, TimeSpan? idleAfter)
    {
        string setting = timeout.Timeout is null
            ? "display-off timeout unreadable (" + timeout.Step + ", Win32 code " + timeout.Code + ")"
            : "display-off timeout " + (timeout.Timeout == TimeSpan.Zero ? "never (0)" : Seconds(timeout.Timeout.Value) + " s " + timeout.Source);
        if (!localSession)
        {
            return new DisplayVerdict(false, "not a local session", "not a local session, so no display to check");
        }

        string lockText = sessionLock.Locked is { } isLocked
            ? (isLocked ? "session locked" : "session unlocked") + " (SessionFlags 0x" + sessionLock.Flags.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")"
            : "session lock state unreadable (" + sessionLock.Step + ", Win32 code " + sessionLock.Code + ", SessionFlags 0x" + sessionLock.Flags.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
        if (sessionLock.Locked is null)
        {
            return new DisplayVerdict(false, "lock state unreadable", lockText + "; " + setting);
        }

        if (sessionLock.Locked == true)
        {
            // A locked console has a display-off timeout of its own, which this does not read, so the scheme's timeout says nothing.
            return new DisplayVerdict(false, "session locked", lockText + "; the locked console has its own display-off timeout; " + setting);
        }

        if (timeout.Timeout is not { } limit)
        {
            return new DisplayVerdict(false, "timeout unreadable", setting);
        }

        if (limit == TimeSpan.Zero)
        {
            return new DisplayVerdict(true, "timeout never", setting);
        }

        if (idleBefore is not { } before || idleAfter is not { } after)
        {
            return new DisplayVerdict(false, "input time unreadable", setting + "; no input time for this session");
        }

        string idle = "last input " + Seconds(before) + " s before the run and " + Seconds(after) + " s at its end, " + setting;
        TimeSpan allowed = limit - Margin;
        return before < allowed && after < allowed
            ? new DisplayVerdict(true, "input within the timeout", idle + " (under it less " + Seconds(Margin) + " s)")
            : new DisplayVerdict(false, "timeout elapsed", idle + " (not under it less " + Seconds(Margin) + " s)");
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
}

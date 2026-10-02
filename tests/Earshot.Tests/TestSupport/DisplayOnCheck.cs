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
internal readonly record struct DisplayOffTimeoutReading(TimeSpan? Timeout, uint Code, string Step, string Source);

// The active power scheme's "Turn off display after" setting, read only: PowerGetActiveScheme for the scheme, then
// PowerReadACValueIndex or PowerReadDCValueIndex for the power source in use (GetSystemPowerStatus: ACLineStatus 1 is online, 0 offline,
// 255 unknown), in the display subgroup (GUID_VIDEO_SUBGROUP 7516b95f-f776-4464-8c53-06167f40cc99, listed on
// https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex). The setting is
// 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e (VIDEOIDLE), in seconds. No Microsoft page found lists that GUID, so it and the unit are from
// `powercfg /q SCHEME_CURRENT SUB_VIDEO VIDEOIDLE` on this machine ("Possible Settings units: Seconds", AC index 0x00000e10). The
// scheme's GUID is freed with LocalFree
// (https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme). A value of 0 is taken as never.
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

internal sealed record DisplayVerdict(bool KnownOn, string Branch, string Reason);

// Whether the display is known to be on, from the display-off timeout and the time since input, both read without the clock under
// test. Known on means the timeout has not run out since the last input, with a margin so a run ending near the boundary is not
// misjudged; a timeout of never is always on. Anything else (a timeout that has run out, or one that could not be read) is unknown,
// and only then may the clock's own account of an unpaced run be believed.
internal static class DisplayState
{
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    internal static DisplayVerdict Decide(bool localSession, DisplayOffTimeoutReading timeout, TimeSpan? idleBefore, TimeSpan? idleAfter)
    {
        string setting = timeout.Timeout is null
            ? "display-off timeout unreadable (" + timeout.Step + ", Win32 code " + timeout.Code + ")"
            : "display-off timeout " + (timeout.Timeout == TimeSpan.Zero ? "never (0)" : Seconds(timeout.Timeout.Value) + " s " + timeout.Source);
        if (!localSession)
        {
            return new DisplayVerdict(false, "not a local session", "not a local session, so no display to check");
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

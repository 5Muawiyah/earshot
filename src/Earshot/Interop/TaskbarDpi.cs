using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The primary taskbar's own DPI: SHAppBarMessage(ABM_GETTASKBARPOS), MonitorFromRect and
// GetDpiForMonitor, falling back to GetDpiForSystem. Moved out of TrayIconFactory (widget-ui.md section
// 4.6) so the gauge and the card use exactly the same route the tray icon already did.
internal static class TaskbarDpi
{
    // The taskbar's effective DPI, or the system DPI with problem set to why.
    public static uint Read(out string? problem)
    {
        problem = null;
        var bar = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (Shell.SHAppBarMessage(Shell.ABM_GETTASKBARPOS, ref bar) == 0)
        {
            // No taskbar, for example while Explorer restarts.
            problem = "SHAppBarMessage(ABM_GETTASKBARPOS) failed, so the system DPI is used";
            return Shell.GetDpiForSystem();
        }

        nint monitor = Shell.MonitorFromRect(in bar.rc, Shell.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            problem = "MonitorFromRect found no monitor for the taskbar, so the system DPI is used";
            return Shell.GetDpiForSystem();
        }

        int hr = Shell.GetDpiForMonitor(monitor, Shell.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        if (hr < 0 || dpiX == 0)
        {
            Earshot.Contracts.StepOutcome step = Earshot.Contracts.StepOutcomes.FromHResult("get-dpi-for-monitor", hr);
            problem = "GetDpiForMonitor returned " + step.CodeName + ", so the system DPI is used";
            return Shell.GetDpiForSystem();
        }

        return dpiX;
    }
}

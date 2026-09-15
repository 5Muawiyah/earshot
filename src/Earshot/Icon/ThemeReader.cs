using System.Security;
using Earshot.Contracts;
using Microsoft.Win32;

namespace Earshot.Icons;

// Picks the glyph ink for the taskbar.
//
//   high contrast on               SystemColors.WindowText
//   SystemUsesLightTheme 0 / other white / black   (the taskbar and Start follow "Windows mode")
//   value missing                  AppsUseLightTheme, the same way
//   both missing                   black, as for a light taskbar
//
// Both values under HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize are
// undocumented, so they are read only and re-read on every WM_SETTINGCHANGE. The documented setting
// they correspond to:
// https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-shell-setup-themes-uwpappsuselighttheme
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.systeminformation.highcontrast
internal sealed class ThemeReader
{
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string SystemUsesLightThemeValue = "SystemUsesLightTheme";
    public const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly ILog _log;
    private string? _lastProblem;

    public ThemeReader(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public Color Ink()
    {
        int? system = null;
        int? apps = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            system = ReadDword(key, SystemUsesLightThemeValue);
            apps = ReadDword(key, AppsUseLightThemeValue);
            _lastProblem = null;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            // Logged once per distinct problem; the ink falls back as documented above.
            string problem = ex.GetType().Name + ": " + ex.Message;
            if (problem != _lastProblem)
            {
                _lastProblem = problem;
                _log.Warn("The taskbar theme could not be read from HKCU\\" + PersonalizeKey + ", so the light theme ink is used.", ex);
            }
        }

        return InkFor(SystemInformation.HighContrast, system, apps, SystemColors.WindowText);
    }

    internal static Color InkFor(bool highContrast, int? systemUsesLightTheme, int? appsUseLightTheme, Color windowText)
    {
        if (highContrast)
        {
            return windowText;
        }

        int? light = systemUsesLightTheme ?? appsUseLightTheme;
        return light is 0 ? Color.White : Color.Black;
    }

    // A REG_DWORD value, or null when the key or value is missing or has another type.
    private static int? ReadDword(RegistryKey? key, string name) =>
        key?.GetValue(name) is int value ? value : null;
}

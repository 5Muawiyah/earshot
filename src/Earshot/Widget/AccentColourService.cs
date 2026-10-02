using System.Runtime.Versioning;
using Earshot.Contracts;
using Windows.UI.ViewManagement;

namespace Earshot.Widget;

// The two shades of the owner's Windows accent colour that Windows itself uses for accent fills: the dark 1
// shade on the light theme and the light 2 shade on the dark theme. That is how Microsoft's own WinUI theme
// defines AccentFillColorDefault (light: SystemAccentColorDark1, dark: SystemAccentColorLight2, in
// microsoft-ui-xaml's Common_themeresources_any.xaml), and with the default blue they are #005FB8 and
// #60CDFF. The palette is read through UISettings.GetColorValue, which Microsoft documents as the
// programmatic way to reach the shades (https://learn.microsoft.com/en-us/windows/apps/develop/ui/theming),
// and a local probe on this PC agreed: GetColorValue(AccentDark1) and (AccentLight2) matched entries 4 and 1
// of HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentPalette.
internal enum AccentShade { Dark1, Light2 }

// Where the accent shades come from. UiSettingsColourSource is the real one; a test supplies a fake.
internal interface IUiColourSource : IDisposable
{
    Color Shade(AccentShade shade);

    // Raised on whatever thread Windows uses, whenever the colours change.
    event EventHandler? ColorValuesChanged;
}

// The accent colour for the current theme, read at run time, kept current when the owner changes it, and
// shared by everything that draws with it (the gauge's ring now, the card's bars next).
internal interface IAccentColours
{
    // The accent fill for the light theme (true) or the dark theme (false).
    Color AccentFor(bool lightTheme);

    // Raised on the UI thread when the owner changes the accent colour.
    event EventHandler? Changed;
}

// Reads the shade for the theme on demand, so a caller always gets the colour Windows has now, and turns
// the colour source's own change event (raised on a Windows thread) into a UI thread Changed event, raised
// only when one of the two shades really differs from the pair last reported: Windows raises
// ColorValuesChanged for other personalisation writes too, with the accent as it was, and a repaint of every
// card for each of those was a visible twitch. The shades are read on demand, never cached for callers; the
// last reported pair is kept only to tell a change from a repeat.
internal sealed class AccentColourService : IAccentColours, IDisposable
{
    private readonly IUiColourSource _source;
    private readonly Action<Action> _uiPost;
    private readonly ILog _log;
    private int _disposed;
    private Color _lastDark1;
    private Color _lastLight2;

    public AccentColourService(IUiColourSource source, Action<Action> uiPost, ILog log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _uiPost = uiPost;
        _log = log;
        _lastDark1 = _source.Shade(AccentShade.Dark1);
        _lastLight2 = _source.Shade(AccentShade.Light2);
        _source.ColorValuesChanged += OnColorValuesChanged;
    }

    public event EventHandler? Changed;

    public Color AccentFor(bool lightTheme) => _source.Shade(lightTheme ? AccentShade.Dark1 : AccentShade.Light2);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.ColorValuesChanged -= OnColorValuesChanged;
        _source.Dispose();
    }

    // The service the gauge uses when nothing supplies one: the real Windows colours, posted to the thread
    // that first asked (the UI thread, since the gauge window is built there). Created once and kept for the
    // life of the process.
    internal static IAccentColours Shared(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (SharedGate)
        {
            if (_shared is null)
            {
                SynchronizationContext? context = SynchronizationContext.Current;
                Action<Action> post = context is null
                    ? static action => action()
                    : action => context.Post(static state => ((Action)state!)(), action);
                _shared = new AccentColourService(new UiSettingsColourSource(), post, log);
            }

            return _shared;
        }
    }

    private static readonly Lock SharedGate = new();
    private static AccentColourService? _shared;

    private void OnColorValuesChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _uiPost(() =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Color dark1 = _source.Shade(AccentShade.Dark1);
            Color light2 = _source.Shade(AccentShade.Light2);
            if (dark1.ToArgb() == _lastDark1.ToArgb() && light2.ToArgb() == _lastLight2.ToArgb())
            {
                return;
            }

            _lastDark1 = dark1;
            _lastLight2 = light2;
            _log.Write(LogLevel.Debug, "The Windows accent colour changed.");
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}

// The real colour source: UISettings.GetColorValue and its ColorValuesChanged event. UISettings is created
// once and kept, since the event lives only as long as the object does.
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.getcolorvalue
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.colorvalueschanged
internal sealed class UiSettingsColourSource : IUiColourSource
{
    // Test seam only: counts real constructions, so a test that builds one on purpose can be told apart from
    // an accidental one. Never read or reset in production.
    internal static int ConstructionCount;

    private readonly UISettings? _settings;
    private readonly Windows.Foundation.TypedEventHandler<UISettings, object>? _handler;
    private EventHandler? _changed;

    public UiSettingsColourSource()
    {
        Interlocked.Increment(ref ConstructionCount);
        if (AccentPlatformGuard.HasUiSettings)
        {
            _settings = new UISettings();
            _handler = (_, _) => _changed?.Invoke(this, EventArgs.Empty);
            _settings.ColorValuesChanged += _handler;
        }
    }

    public event EventHandler? ColorValuesChanged
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public Color Shade(AccentShade shade)
    {
        if (!AccentPlatformGuard.HasUiSettings || _settings is null)
        {
            return DefaultShade(shade);
        }

        Windows.UI.Color c = _settings.GetColorValue(shade == AccentShade.Dark1 ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
        return Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    public void Dispose()
    {
        if (AccentPlatformGuard.HasUiSettings && _settings is not null && _handler is not null)
        {
            _settings.ColorValuesChanged -= _handler;
        }
    }

    // The Windows default blue's two shades, used only where the palette cannot be read at all (a Windows
    // build without UISettings); a machine that has it always answers with the owner's own colour.
    internal static Color DefaultShade(AccentShade shade) =>
        shade == AccentShade.Dark1 ? Color.FromArgb(0x00, 0x5F, 0xB8) : Color.FromArgb(0x60, 0xCD, 0xFF);
}

internal static class AccentPlatformGuard
{
    // UISettings.GetColorValue and ColorValuesChanged exist from the first Windows 10 SDK; 10.0.19041.0 is
    // this project's own build floor, used so every WinRT guard in the solution checks the same version.
    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    internal static bool HasUiSettings { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}

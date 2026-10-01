using Earshot.Contracts;
using Windows.UI.ViewManagement;

namespace Earshot.Widget;

// What the card reads from Windows to look right: the text size, whether transparency effects are on and
// whether a high-contrast theme is in force. The theme itself (light or dark) and the accent colour come from the
// readings the card already takes.
internal readonly record struct SystemLook(double TextScale, bool Transparency, bool HighContrast)
{
    // No scaling, effects on, no high contrast: the look of a machine that has changed nothing.
    public static SystemLook Default { get; } = new(1.0, Transparency: true, HighContrast: false);

    // Text size the Settings page can ask for runs from 100% to 225%.
    // https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.textscalefactorchanged
    public SystemLook Clamped() => this with { TextScale = Math.Clamp(double.IsFinite(TextScale) ? TextScale : 1.0, TypeRamp.MinTextScale, TypeRamp.MaxTextScale) };

    // Everything that fills the card with its own colour instead of the backdrop.
    public bool OpaqueBackground => HighContrast || !Transparency;
}

// Where the look comes from. UiSettingsLookSource is the real one; a test supplies a fake.
internal interface ISystemLookSource : IDisposable
{
    double TextScaleFactor { get; }

    // Settings > Personalisation > Colours > Transparency effects.
    bool AdvancedEffectsEnabled { get; }

    // Raised on whatever thread Windows uses.
    event EventHandler? Changed;
}

// The look for the card, read at run time and kept current. The source raises its event on a Windows thread;
// this posts it to the UI thread. Whether UISettings raises these events in a process with no core window is not
// documented, so a caller also asks for the look afresh on every show and on every WM_SETTINGCHANGE (Poke); either
// path is enough.
internal sealed class SystemLookService : IDisposable
{
    private readonly ISystemLookSource _source;
    private readonly Func<bool> _highContrast;
    private readonly Action<Action> _uiPost;
    private readonly ILog _log;
    private int _disposed;
    private SystemLook _last;

    public SystemLookService(ISystemLookSource source, Func<bool> highContrast, Action<Action> uiPost, ILog log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(highContrast);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _highContrast = highContrast;
        _uiPost = uiPost;
        _log = log;
        _last = Current;
        _source.Changed += OnSourceChanged;
    }

    // Raised on the UI thread, once per real change of what Current reads.
    public event EventHandler? Changed;

    public SystemLook Current => new SystemLook(_source.TextScaleFactor, _source.AdvancedEffectsEnabled, _highContrast()).Clamped();

    // Reads the look again and raises Changed when it differs from the last one raised. Called on the UI thread
    // from WM_SETTINGCHANGE, where a high-contrast or text size change also shows up.
    public void Poke()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        SystemLook now = Current;
        if (now == _last)
        {
            return;
        }

        _last = now;
        _log.Write(LogLevel.Debug, "The Windows look changed: text size " + now.TextScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", transparency " + now.Transparency + ", high contrast " + now.HighContrast + ".");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _source.Changed -= OnSourceChanged;
        _source.Dispose();
    }

    // The service the card uses when nothing supplies one, posted to the thread that first asked (the UI thread,
    // since the card is built there).
    internal static SystemLookService Shared(ILog log)
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
                _shared = new SystemLookService(new UiSettingsLookSource(), static () => SystemInformation.HighContrast, post, log);
            }

            return _shared;
        }
    }

    private static readonly Lock SharedGate = new();
    private static SystemLookService? _shared;

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _uiPost(Poke);
    }
}

// The real source: UISettings.TextScaleFactor and AdvancedEffectsEnabled with their change events. UISettings is
// created once and kept, since the events live only as long as the object does.
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.textscalefactor
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.uisettings.advancedeffectsenabled
internal sealed class UiSettingsLookSource : ISystemLookSource
{
    private readonly UISettings? _settings;
    private readonly Windows.Foundation.TypedEventHandler<UISettings, object>? _scaleHandler;
    private readonly Windows.Foundation.TypedEventHandler<UISettings, object>? _effectsHandler;
    private EventHandler? _changed;

    public UiSettingsLookSource()
    {
        if (AccentPlatformGuard.HasUiSettings)
        {
            _settings = new UISettings();
            _scaleHandler = (_, _) => _changed?.Invoke(this, EventArgs.Empty);
            _effectsHandler = (_, _) => _changed?.Invoke(this, EventArgs.Empty);
            _settings.TextScaleFactorChanged += _scaleHandler;
            _settings.AdvancedEffectsEnabledChanged += _effectsHandler;
        }
    }

    public event EventHandler? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public double TextScaleFactor => AccentPlatformGuard.HasUiSettings && _settings is not null ? _settings.TextScaleFactor : 1.0;

    public bool AdvancedEffectsEnabled => !AccentPlatformGuard.HasUiSettings || _settings is null || _settings.AdvancedEffectsEnabled;

    public void Dispose()
    {
        if (AccentPlatformGuard.HasUiSettings && _settings is not null)
        {
            if (_scaleHandler is not null)
            {
                _settings.TextScaleFactorChanged -= _scaleHandler;
            }

            if (_effectsHandler is not null)
            {
                _settings.AdvancedEffectsEnabledChanged -= _effectsHandler;
            }
        }
    }
}

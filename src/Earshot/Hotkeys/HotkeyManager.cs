using System.Globalization;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Hotkeys;

// Registers and releases the owner's global shortcuts against the host's own hidden window, and raises
// Activated when one of them fires. This class is not thread-safe and does not pretend to be: Apply,
// ReleaseAll and Dispose must run on the thread that owns the window (RegisterHotKey "fails if you try
// to associate a hot key with a window created by another thread", and UnregisterHotKey "Frees a hot key
// previously registered by the calling thread"), and MessageReceived, and therefore Activated, is raised
// on that same thread by the host's own message pump.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey
//
// Nothing here blocks: no Thread.Sleep, no Task.Wait, no lock held across a native call, no timer, no
// debounce. MOD_NOREPEAT (added once here, at the call) is the documented answer to auto-repeat, and a
// pump that stops pumping freezes the tray. Activated handlers run on the pump too; slow work belongs
// elsewhere, and this class must not hide that cost by queueing the handler onto another thread, which
// would reorder the owner's key presses.
public sealed class HotkeyManager : IDisposable
{
    // Base of the hot key ids: 0x4A00 through 0x4A03, one per HotkeyAction, inside the application
    // range 0x0000 to 0xBFFF that RegisterHotKey requires (0xC000 and above is reserved for shared
    // DLLs, which take theirs from GlobalAddAtom).
    public const int HotkeyIdBase = 0x4A00;

    // MOD_NOREPEAT. Not a HotkeyModifiers member: it is never something the owner chooses, it is not
    // part of the shortcut text, and it never comes back in WM_HOTKEY's lParam low word (which the
    // platform documents as MOD_ALT, MOD_CONTROL, MOD_SHIFT and MOD_WIN only). Added once, here.
    private const uint ModNoRepeat = 0x4000;

    // The F12 virtual key. "The F12 key is reserved for use by the debugger at all times, so it should
    // not be registered as a hot key."
    private const ushort VkF12 = 0x7B;

    private static readonly HotkeyAction[] ActionOrder =
    [
        HotkeyAction.ToggleConnection,
        HotkeyAction.ToggleAudioProtection,
        HotkeyAction.ToggleBlockAtBoot,
        HotkeyAction.SpeakStatus,
    ];

    private readonly IMessageWindow _window;
    private readonly INativeHotkeys _native;
    private readonly ILog _log;
    private readonly Dictionary<int, HotkeyAction> _held = new();

    private IReadOnlyList<HotkeyRegistrationOutcome> _currentOutcomes = Array.Empty<HotkeyRegistrationOutcome>();
    private bool _messageHandlerAttached;
    private bool _disposed;

    public HotkeyManager(IMessageWindow window, INativeHotkeys native, ILog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(log);

        _window = window;
        _native = native;
        _log = log;
        _window.MessageReceived += OnMessageReceived;
        _messageHandlerAttached = true;
    }

    public event EventHandler<HotkeyActivatedEventArgs>? Activated;

    // The outcomes of the last Apply. Empty before the first one.
    public IReadOnlyList<HotkeyRegistrationOutcome> CurrentOutcomes => _currentOutcomes;

    // Releases what is held, then registers what the settings ask for. Returns one outcome per action,
    // in HotkeyAction order.
    public IReadOnlyList<HotkeyRegistrationOutcome> Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_window.IsOwnedByCurrentThread)
        {
            throw new InvalidOperationException("Hotkeys must be applied on the thread that owns the window.");
        }

        ReleaseAllInternal();

        var outcomes = new List<HotkeyRegistrationOutcome>(ActionOrder.Length);
        if (!settings.Enabled)
        {
            foreach (HotkeyAction action in ActionOrder)
            {
                outcomes.Add(new HotkeyRegistrationOutcome(action, settings.TextFor(action), HotkeyRegistrationState.NotSet, 0, "Shortcuts are switched off."));
            }

            _currentOutcomes = outcomes;
            return outcomes;
        }

        var claimed = new List<HotkeyCombination>(ActionOrder.Length);
        foreach (HotkeyAction action in ActionOrder)
        {
            string text = settings.TextFor(action);
            outcomes.Add(ApplyOne(action, text, claimed));
        }

        _currentOutcomes = outcomes;
        return outcomes;
    }

    // Unregisters every held id, logs each call, empties the held set, and leaves CurrentOutcomes
    // alone. Safe to call when nothing is held, and safe to call twice.
    public void ReleaseAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseAllInternal();
    }

    // Unsubscribes from MessageReceived first, then releases everything held, then marks the instance
    // disposed. Idempotent: a second call does nothing and logs nothing. There is no finaliser: the
    // handle is not ours, and the unregister must happen on the window's thread, which a finaliser
    // cannot promise.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_messageHandlerAttached)
        {
            _window.MessageReceived -= OnMessageReceived;
            _messageHandlerAttached = false;
        }

        ReleaseAllInternal();
        _disposed = true;
    }

    private HotkeyRegistrationOutcome ApplyOne(HotkeyAction action, string text, List<HotkeyCombination> claimed)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.NotSet, 0, "No shortcut set.");
        }

        if (!HotkeyText.TryParse(text, out HotkeyCombination combination, out string parseError))
        {
            return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.TextRejected, 0, parseError);
        }

        if (combination.VirtualKey == VkF12)
        {
            return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.Refused, 0,
                "F12 is kept by Windows for the debugger, so it cannot be a shortcut.");
        }

        string canonical = HotkeyText.Format(combination);
        if (claimed.Contains(combination))
        {
            return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.DuplicateInSettings, 0,
                canonical + " is already set for another command here.");
        }

        int id = HotkeyIdBase + (int)action;
        uint modifiers = (uint)combination.Modifiers | ModNoRepeat;
        NativeCallResult result = _native.Register(_window.Handle, id, modifiers, combination.VirtualKey);
        LogRegister(id, modifiers, combination.VirtualKey, result);

        if (result.Succeeded)
        {
            _held[id] = action;
            claimed.Add(combination);
            return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.Registered, 0, canonical + " is set.");
        }

        if (result.ErrorCode == 1409)
        {
            return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.AlreadyHeld, 1409,
                canonical + " is already in use by another program, so it was not set. Windows reported error 1409.");
        }

        return new HotkeyRegistrationOutcome(action, text, HotkeyRegistrationState.Failed, result.ErrorCode,
            canonical + " could not be set. Windows reported error " + result.ErrorCode.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private void ReleaseAllInternal()
    {
        if (_held.Count == 0)
        {
            return;
        }

        foreach (int id in _held.Keys.ToArray())
        {
            NativeCallResult result = _native.Unregister(_window.Handle, id);
            LogUnregister(id, result);
        }

        _held.Clear();
    }

    private void OnMessageReceived(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message != NativeMethods.WM_HOTKEY)
        {
            return;
        }

        int id = unchecked((int)e.WParam);
        if (_held.TryGetValue(id, out HotkeyAction action))
        {
            Activated?.Invoke(this, new HotkeyActivatedEventArgs(action));
            return;
        }

        _log.Write(LogLevel.Debug, "WM_HOTKEY ignored id=" + id.ToString(CultureInfo.InvariantCulture));
    }

    // "RegisterHotKey id=0x4A00 modifiers=0x4003 vk=0x50 result=ok" or
    // "RegisterHotKey id=0x4A01 modifiers=0x4006 vk=0x4F result=failed error=1409".
    private void LogRegister(int id, uint modifiers, ushort virtualKey, NativeCallResult result)
    {
        string line = "RegisterHotKey id=" + Hex(id) + " modifiers=" + Hex(modifiers) + " vk=" + Hex(virtualKey) +
            " result=" + (result.Succeeded ? "ok" : "failed error=" + result.ErrorCode.ToString(CultureInfo.InvariantCulture));
        _log.Write(LogLevel.Debug, line);
    }

    // "UnregisterHotKey id=0x4A00 result=ok" or "UnregisterHotKey id=0x4A02 result=failed error=1419".
    private void LogUnregister(int id, NativeCallResult result)
    {
        string line = "UnregisterHotKey id=" + Hex(id) +
            " result=" + (result.Succeeded ? "ok" : "failed error=" + result.ErrorCode.ToString(CultureInfo.InvariantCulture));
        _log.Write(LogLevel.Debug, line);
    }

    // Lower-case "0x" with upper-case digits, from $"0x{value:X}".
    private static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);
}

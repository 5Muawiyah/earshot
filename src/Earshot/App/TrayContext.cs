using System.Drawing;
using System.Globalization;
using Earshot.Audio.Connect;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Icons;
using Earshot.Infra;
using Earshot.Streaming;
using Earshot.Tray;
using Earshot.Voice;

namespace Earshot.App;

// What the tray needs to know about how it was started.
internal sealed record TrayStartOptions(
    bool FirstRun,                     // settings.json did not exist and defaults were just written
    SettingsLoadStatus SettingsStatus,
    string? ExePath,                   // for the Open on startup command line
    IStartupRegistry StartupRegistry)
{
    // How long Exit waits for actions already in flight before it ends the message loop.
    public TimeSpan ExitWaitLimit { get; init; } = TrayContext.DefaultExitWaitLimit;

    // How long Exit waits in all while the block coordinator still has work in flight after ExitWaitLimit.
    public TimeSpan CoordinatorExitWaitLimit { get; init; } = TrayContext.DefaultCoordinatorExitWaitLimit;

    // How long Exit keeps the loop running after a card it shows as Earshot closes, so the card can be read.
    public TimeSpan ExitNoticeTime { get; init; } = TrayContext.DefaultExitNoticeTime;

    // False only in tests, so they add no icon to the user's notification area.
    public bool ShowIcon { get; init; } = true;

    // True when Windows started Earshot from its Run value (--startup).
    public bool StartedAtLogon { get; init; }

    // True when EARSHOT_DATA_ROOT points Earshot's folders somewhere else, so this run must not write the
    // owner's real Run value.
    public bool DataRootRedirected { get; init; }

    // Where the pointer is, read when a click arrives so a card that comes later still lands at the click.
    public Func<Point> CursorPosition { get; init; } = () => Cursor.Position;

    // Milliseconds since some fixed point, read when a click arrives.
    public Func<long> TickCount { get; init; } = () => Environment.TickCount64;

    // Clicks this close together are one click as the user meant it (a double or triple click).
    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.systeminformation.doubleclicktime
    public TimeSpan DoubleClickTime { get; init; } = TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime);

    // The installed copy (%ProgramFiles%\Earshot\Earshot.exe), which Open on startup starts once it exists, or null.
    public string? InstalledExePath { get; init; }

    // Whether a file exists, for the installed copy.
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    // The RegisterHotKey/UnregisterHotKey caller HotkeyManager uses, injected the way IStartupRegistry is:
    // the real one by default, a fake in tests, so a tray-level test never registers a real global hotkey.
    public INativeHotkeys NativeHotkeys { get; init; } = new User32Hotkeys();

    // Builds the speech engine VoiceOver opens against, injected the same way: the real one by default, a
    // fake in tests, so a tray-level test never constructs a real SpeechSynthesizer. Called from
    // ApplyVoiceOver only when settings ask for a running announcer that is not running yet.
    public Func<ISpeechEngine> VoiceEngineFactory { get; init; } = static () => new SystemSpeechEngine();

    // Builds the platform Play from a phone runs against, injected the same way: the real one by default, a fake
    // in tests, so a tray-level test never touches WinRT, never enumerates a device and never opens a connection.
    // Called from ApplyStreaming only when settings ask for the feature, which by default they do not.
    public Func<ILog, IStreamingPlatform> StreamingPlatformFactory { get; init; } = static log => new WindowsStreamingPlatform(log);

    // How long closing waits for the streaming connection to be let go before the process ends anyway.
    public TimeSpan StreamingShutdownWait { get; init; } = TrayContext.DefaultStreamingShutdownWait;

    // The budgets HandBackAsync runs the shut-down hand-back to: a 4 s cap, 1.5 s of it for the disconnect.
    // Injectable so a test can shrink them.
    public TimeSpan HandBackBudget { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan DisconnectHandBackWait { get; init; } = TimeSpan.FromMilliseconds(1500);

    // The same for sleep: a 1.5 s cap, 0.75 s of it for the disconnect.
    public TimeSpan SleepHandBackBudget { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan SleepDisconnectWait { get; init; } = TimeSpan.FromMilliseconds(750);

    // The clock HoldReply checks its deadline against. TimeProvider.System by default; a test can inject one it
    // drives, so a cut-short hold is provable without a real multi-second wait.
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// The tray: icon, tooltip, menu, left click and the cards they raise. Runs on the UI thread only.
//
// Every device action goes through the BlockCoordinator, which owns connect, disconnect, block, allow and
// protection order and shows the cards for them. The tray decides what a click means, shows what it decides
// before handing over (nothing found, busy, settings), and logs every result with its steps.
//
// Left click: MouseClick with the left button only (Click and MouseClick also fire for the right and
// middle buttons, with X = Y = 0), ignored with a card while a menu action is in flight, and ignored while the
// device itself reports the link is changing, which is when the menu item is disabled too. A click while the
// coordinator finishes work of its own (the start-up check, an automatic block, the session-end block) waits for
// it, with a card at once so the click is never left unanswered; the menu item is disabled then. A click while a
// connect or disconnect is in flight is a newer intent: the one in flight is cancelled and, once its clean-up
// (a re-block included) has finished, the opposite runs, with a card at once as for the coordinator's own work.
// Clicks within the double-click time of the one that started it are the same click, so a fast double or triple
// click never toggles twice.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.mouseclick
//
// Cancellation: each connect or disconnect has its own token, cancelled by a newer click, when a different
// device is chosen, or when Earshot closes. Each menu action (setup, Block at boot, protection, device change)
// has its own token too, cancelled only when Earshot closes, so a click never cancels one. A cancellation stops
// the coordinator's waits, never a gate change it has already sent: that is waited for before the clean-up.
// Nothing here reads IBlockController.IsSetUp: that checks a scheduled task, which is not UI thread work,
// so setup is decided from the boot block status the coordinator reads asynchronously.
//
// Exit: no new input, cancel what is in flight, then wait for it and for the coordinator (at most
// ExitWaitLimit) before the message loop ends, so a cancelled connect can finish putting the device nodes
// back to blocked and the coordinator can block enabled nodes that are not in use. When the coordinator is still
// busy then (a gate change it sent is still running, and its block follows only once that has ended), Exit keeps
// waiting for the coordinator alone, up to CoordinatorExitWaitLimit in all: every wait inside it has a limit of its
// own, and a process that has ended can no longer send the block that keeps the nodes disabled at rest. The Exit
// click shows a card that Earshot waits for the current change only when a change other than the check before
// closing is in flight; "Blocking" is shown only once the coordinator is about to send the block. What the user should
// know as Earshot closes (it closed while the AirPods were in use, before a change finished, or that block
// did not take) is shown at the Exit click and kept up for ExitNoticeTime.
internal sealed class TrayContext : ApplicationContext
{
    public const string SomethingWentWrongMessage = "Something went wrong. See the log.";
    public const string SettingsNotSavedMessage = "Settings could not be saved.";
    public const string SettingsRestoredMessage = "Settings were restored from a backup.";
    public const string SettingsResetMessage = "Settings were reset.";
    public const string SettingsUnreadableMessage = "Settings could not be read.";
    public const string SettingsNewerMessage = "Settings are from a newer Earshot. Changes will not be saved.";
    public const string BusyMessage = "Another change is still running. Try again shortly.";
    public const string FinishingFirstMessage = "Finishing another change first.";
    public const string ClosingMessage = "Closing once the current change finishes.";
    public const string HandingBackMessage = "Earshot is handing the AirPods back.";
    public const string BlockingBeforeClosingMessage = "Blocking the AirPods before closing.";
    public const string BlockStatusUnreadableMessage = "Could not read the boot block status.";
    public const string GateNotRepinnedMessage = "Device saved. Boot block may still use the previous device.";
    public const string BlockAtBootHotkeyOffRefusedMessage = "Turning off block at boot needs the menu, not a shortcut.";
    public const string HotkeySetupNeededMessage = "Set up Earshot from the menu first.";
    public const string HotkeyOthersNotSetSuffix = " Others were not set either. See the log.";

    // Long enough for a cancelled connect to finish its own clean-up, which may run the gate once more to
    // block the device nodes; short enough that a controller that never returns cannot keep Earshot
    // running. Whatever is still in flight when it runs out is logged by name.
    public static readonly TimeSpan DefaultExitWaitLimit = TimeSpan.FromSeconds(30);

    // How long Exit waits in all for the block coordinator once DefaultExitWaitLimit has run out with it still busy.
    // The coordinator waits for no gate run longer than TaskSchedulerGate allows it, and for no device read longer
    // than its own budgets, so its work ends; the longest sequence that can still be in flight at Exit is a protect
    // verb it must wait for, then the block after it. A waiting budget, not a measured figure.
    public static readonly TimeSpan DefaultCoordinatorExitWaitLimit =
        Boot.TaskSchedulerGate.ProtectTimeout + Boot.TaskSchedulerGate.GateTimeout + TimeSpan.FromMinutes(1);

    // How long a card shown as Earshot closes stays readable before the process ends: about one card's own
    // dismiss time. A UI timing choice, not a measurement.
    public static readonly TimeSpan DefaultExitNoticeTime = TimeSpan.FromSeconds(4);

    // How long closing waits for Windows to let go of the streaming connection(s) Close itself starts releasing:
    // the one in use now, and any earlier one a switch-off could not get Windows to confirm releasing, tried
    // again here. It does not bound a release already under way in _pending from an earlier switch-off
    // (StopStreaming(wait: false, ...)): Exit awaits everything in _pending up to its own, much longer limit
    // (DefaultExitWaitLimit) before Close ever runs. No page gives Dispose a time limit, so this is the limit for
    // what Close itself starts: a waiting budget, not a measured figure. The work runs on a pool thread, which
    // cannot keep the process alive once this runs out.
    public static readonly TimeSpan DefaultStreamingShutdownWait = TimeSpan.FromSeconds(2);

    private readonly ServiceRegistry _registry;
    private readonly BlockCoordinator _coordinator;
    private readonly ILog _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;
    private readonly ShellMessageWindow _window;
    private readonly HotkeyManager _hotkeys;
    private readonly Func<ISpeechEngine> _voiceEngineFactory;
    private readonly Func<ILog, IStreamingPlatform> _streamingPlatformFactory;
    private readonly HostBusyGate _streamingGate = new();
    private readonly TimeSpan _streamingShutdownWait;
    private readonly TimeSpan _handBackBudget;
    private readonly TimeSpan _disconnectHandBackWait;
    private readonly TimeSpan _sleepHandBackBudget;
    private readonly TimeSpan _sleepDisconnectWait;
    private readonly TimeProvider _time;
    private readonly TrayIconFactory _icons;
    private readonly StartupRegistration _startup;
    private readonly BluetoothDeviceList _devices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _exitWaitLimit;
    private readonly TimeSpan _coordinatorExitWaitLimit;
    private readonly TimeSpan _exitNoticeTime;
    private readonly Func<Point> _cursorPosition;
    private readonly Func<long> _tickCount;
    private readonly TimeSpan _doubleClickTime;
    private readonly bool _startedAtLogon;

    // Actions started from the icon or the menu that have not finished, by name.
    private readonly Dictionary<Task, string> _pending = new();

    private DeviceSnapshot _snapshot;
    private StartupState _startupState;
    private CancellationTokenSource? _toggle;
    private bool _toggleInFlight;

    // The connect or disconnect in flight: which it is, when its click came, whether a newer click is waiting for
    // it to end, why it was cancelled, and a task that completes once it has ended, clean-up included.
    private bool _toggleConnect;
    private long _toggleClickedAt;
    private bool _toggleSuperseded;
    private string? _toggleCancelReason;
    private TaskCompletionSource? _toggleDone;
    private int _operationsInFlight;
    private Guid _pinAttemptedFor;
    private DevicePickerForm? _picker;
    private CardPlace? _exitPlace;
    private bool _blockingCardShown;
    private bool _closing;
    private bool _closed;

    // The shortcut problems the last card was shown for (ApplyHotkeys), "" when there were none.
    private string _hotkeyProblemsShown = "";

    // VoiceOver: the running announcer, or null while it is off, and the settings it was last opened
    // with, so a settings change that asks for nothing different never reopens the speech engine.
    private SpeechAnnouncer? _voice;
    private VoiceOverSettings? _voiceApplied;

    // Play from a phone: the running coordinator, or null while the feature is off (the default) or still being
    // started; the settings it was last applied with, so a change that asks for nothing different restarts nothing; a count that a start still under way compares, to see it is no longer
    // wanted; and whether a start or a read of the list is in flight, so a second click starts no second one.
    private StreamingCoordinator? _streaming;
    private StreamingSettings? _streamingApplied;
    private int _streamingGeneration;
    private bool _streamingStartInFlight;
    private bool _streamingRefreshInFlight;

    // Connections an earlier switch-off could not get Windows to confirm releasing, kept instead of let go with
    // the coordinator that saw the failure, because only the platform instance that coordinator holds still has
    // the connection to try again. Close makes that one further attempt, through the same instance, once each;
    // nothing here retries one on a later switch back on. Read and written on the UI thread only.
    private readonly List<StreamingCoordinator> _unconfirmedStreaming = new();

    // Sticky for the life of the process: once Open has told us there is no usable voice on this
    // machine (StepOutcomes.NotAvailable), that is a machine fact that will not change while Earshot
    // runs, so the menu keeps showing "(no voice)" without probing again on every settings change.
    private bool _voiceKnownNoVoice;

    public TrayContext(ServiceRegistry registry, BlockCoordinator coordinator, TrayStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _coordinator = coordinator;
        _log = registry.Log;
        _exitWaitLimit = options.ExitWaitLimit;
        _coordinatorExitWaitLimit = options.CoordinatorExitWaitLimit;
        _exitNoticeTime = options.ExitNoticeTime;
        _cursorPosition = options.CursorPosition;
        _tickCount = options.TickCount;
        _doubleClickTime = options.DoubleClickTime;
        _startedAtLogon = options.StartedAtLogon;
        _snapshot = registry.Monitor.Current;
        _devices = new BluetoothDeviceList(_log);
        _startup = new StartupRegistration(options.StartupRegistry, _log, registry.SafeMode, options.ExePath, options.DataRootRedirected,
            options.InstalledExePath, options.FileExists);
        _icons = new TrayIconFactory(_log, new ThemeReader(_log));

        _window = new ShellMessageWindow(_log);
        // The shell re-adds the old icon after TaskbarCreated, so that one always swaps in a new icon.
        _window.TaskbarCreated += (_, _) => RefreshIcon(remeasure: true, force: true);
        _window.SettingChanged += (_, _) => RefreshIcon(remeasure: true, force: false);
        _window.DisplayChanged += (_, _) => RefreshIcon(remeasure: true, force: false);
        _window.SessionEnding += OnSessionEnding;
        _window.PowerChanged += OnPowerChanged;

        // WM_CLOSE asks Earshot to close, so it takes the same orderly path as Exit on the menu, the block before
        // closing included. There is no click to place a card by.
        _window.CloseRequested += (_, _) => _ = ExitAsync(CardPlace.NearTray, "Windows asked Earshot to close (WM_CLOSE).");

        // Global shortcuts, over the same hidden window: no second window, no second message pump.
        // Every action a shortcut can raise goes through the exact method the matching menu item or the
        // left click already uses, so a hotkey is never a second route to the device.
        _hotkeys = new HotkeyManager(_window, options.NativeHotkeys, _log);
        _hotkeys.Activated += OnHotkeyActivated;
        _voiceEngineFactory = options.VoiceEngineFactory;
        _streamingPlatformFactory = options.StreamingPlatformFactory;
        _streamingShutdownWait = options.StreamingShutdownWait;
        _handBackBudget = options.HandBackBudget;
        _disconnectHandBackWait = options.DisconnectHandBackWait;
        _sleepHandBackBudget = options.SleepHandBackBudget;
        _sleepDisconnectWait = options.SleepDisconnectWait;
        _time = options.Time;

        _menu = new TrayMenu(CurrentMenuState);
        _menu.ToggleClicked += (_, _) => StartToggle();
        _menu.PlayFromPhoneItemClicked += OnPlayFromPhoneItemClicked;
        _menu.BlockAtBootClicked += (_, _) => Start("block at boot", place => BlockAtBootAsync(place));
        _menu.HandBackClicked += (_, _) => OnHandBackClicked();
        _menu.ProtectAudioClicked += (_, _) => OnProtectAudioClicked();
        _menu.OpenOnStartupClicked += (_, _) => OnOpenOnStartupClicked();
        _menu.SpeakStatusClicked += (_, _) => OnSpeakStatusClicked();
        // The click point is read now, before the menu closes and the picker opens.
        _menu.ChooseDeviceClicked += (_, _) =>
        {
            CardPlace place = ClickPlace();
            _registry.UiPost(() => Launch("choose device", () => ChooseDeviceAsync(place), place));
        };
        _menu.SetUpClicked += (_, _) => Start("setup", RunSetupAsync);
        // The click point is read now, for a card that follows the Exit click.
        _menu.ExitClicked += (_, _) =>
        {
            CardPlace place = ClickPlace();
            _ = ExitAsync(place);
        };

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu.Strip };
        _notifyIcon.MouseClick += OnIconMouseClick;
        _notifyIcon.MouseDown += (_, _) => _ = _coordinator.RefreshStatusAsync();
        _notifyIcon.MouseDown += OnIconMouseDownForStreaming;
        UpdatePresentation(forceIcon: true);
        _notifyIcon.Visible = options.ShowIcon;

        _registry.Monitor.SnapshotChanged += OnSnapshotChanged;
        _registry.Settings.Changed += OnSettingsChanged;
        _coordinator.Changed += OnCoordinatorChanged;

        _startupState = _startup.Read();
        if (options.FirstRun)
        {
            ApplyStartupDefault();
        }
        else
        {
            RepairStartupIfStale(options.SettingsStatus);
        }

        ReportSettingsLoad(options.SettingsStatus);
        ApplyHotkeys();
        ApplyVoiceOver();
        ApplyStreaming();
        _ = _coordinator.RefreshStatusAsync();
        _ = PinIfFirstSightingAsync();
    }

    // True while a connect, disconnect or menu action started here is in flight.
    internal bool IsBusy => _toggleInFlight || _operationsInFlight > 0;

    // True while anything at all is in flight, the coordinator's own work included.
    internal bool IsWorking => IsBusy || _pending.Count > 0 || _coordinator.IsBusy;

    // Actions from the icon or the menu that have not finished yet.
    internal int PendingActions => _pending.Count;

    internal TrayMenu Menu => _menu;

    // The tooltip as it stands, for tests.
    internal string TooltipText => _notifyIcon.Text;

    // Shows the current state on a card, for example when a second copy of Earshot is started. It follows the
    // user's own action, so it is placed like a card after a click. While Exit waits for a change in flight the icon
    // is already gone and this process still holds the single-instance lock, so the second copy has exited: the card
    // then says Earshot is closing, rather than leave the start unanswered.
    public void ShowStatusCard()
    {
        if (_closed)
        {
            _log.Info("Earshot was started again after it closed; no card is shown.");
            return;
        }

        if (_closing)
        {
            _log.Info("Earshot was started again while it is closing; it says so on a card.");
            CardPlace.NearCursor.Show(_registry.Cards, TrayStatus.AppName, ClosingMessage);
            return;
        }

        EarshotSettings settings = _registry.Settings.Current;
        ShowCard(TrayStatus.DeviceName(_snapshot, settings), TrayStatus.CardStatus(_snapshot, BlockStatus, settings), CardPlace.NearCursor);
    }

    // Shows an unexpected exception from any handler on the tray thread. Program.Tray routes
    // Application.ThreadException here.
    internal void ReportUnexpected(string action, Exception ex, CardPlace place)
    {
        _log.Error(action + ": unexpected error.", ex);
        ShowCard(TrayStatus.AppName, SomethingWentWrongMessage, place);
    }

    // The NotifyIcon.MouseClick handler. Internal so tests can raise each button.
    internal void OnIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        StartToggle();
    }

    protected override void ExitThreadCore()
    {
        Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            _registry.Monitor.SnapshotChanged -= OnSnapshotChanged;
            _registry.Settings.Changed -= OnSettingsChanged;
            _coordinator.Changed -= OnCoordinatorChanged;
            _window.SessionEnding -= OnSessionEnding;
            _window.PowerChanged -= OnPowerChanged;
            _notifyIcon.MouseClick -= OnIconMouseClick;
            _notifyIcon.MouseDown -= OnIconMouseDownForStreaming;
            _menu.PlayFromPhoneItemClicked -= OnPlayFromPhoneItemClicked;
            _hotkeys.Activated -= OnHotkeyActivated;

            // NotifyIcon.Dispose removes the icon from the notification area. The Icon it used is
            // disposed only after that.
            Icon? icon = _notifyIcon.Icon;
            _notifyIcon.ContextMenuStrip = null;
            _notifyIcon.Dispose();
            icon?.Dispose();
            _menu.Dispose();
            _window.Dispose();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private BootBlockStatus? BlockStatus => _coordinator.BlockStatus;

    // The hidden window, for tests that drive its window procedure.
    internal ShellMessageWindow Window => _window;

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        // Exit has already begun the coordinator's shutdown, block before closing included. Any other way here
        // (the loop ending, Dispose) only stops it: nothing would be left to run a block.
        _closing = true;
        _closed = true;
        _coordinator.Stop();
        _lifetime.Cancel();
        _registry.Cards.Hide();
        _notifyIcon.Visible = false;

        // Every orderly exit path runs through here (ExitThreadCore, Dispose), so a shortcut is never left
        // registered after one of those. This runs on the UI thread, which owns the window. It does not
        // run at all for a crash or a kill: nothing in the process can release a hot key once the process
        // is gone, and Windows does not document releasing one for a dead owner either (HotkeyManager's
        // own header notes UnregisterHotKey must be called explicitly).
        _hotkeys.Dispose();

        // Cancels and disposes the speech engine on every exit path this handles (ExitThreadCore,
        // Dispose), the same way: a phrase in flight must not keep the process alive, and StopSpeaking() is what
        // cancels it (drops the mailbox, waits at most ShutdownWaitMilliseconds, then disposes).
        //
        // This blocks the UI thread for up to ShutdownWaitMilliseconds if the worker is still inside
        // Speak when a session ends: accepted, not a bug. It happens at most once, after the coordinator
        // has already stopped and every other shutdown step above has run, the bound is the owner's own
        // setting (VoiceOverSettings.ShutdownWaitMilliseconds, clamped to at most 10 seconds), and the
        // alternative (not joining at all) risks disposing a synthesiser that is still mid-call.
        StopVoice();

        // Play from a phone: every connection this run enabled is asked to be released here, on every exit path
        // this handles: the one in use now, if there is one, and any earlier one a switch-off could not get
        // Windows to confirm releasing, tried again through the same platform instance that still holds it,
        // because "the underlying transport is deactivated when all references are released" and a PC still
        // accepting audio after Earshot has gone is the worst thing this feature could do. The release runs on a
        // pool thread and this waits for it at most StreamingShutdownWait, for all of it together, not once per
        // connection: no page gives Dispose a time limit, and a pool thread cannot keep the process alive.
        // Whatever Windows still has not confirmed by the time that runs out has its reference end with the
        // process, same as it always has. A start still in flight was cancelled by _lifetime above and releases
        // whatever it had enabled when it comes back. There is no watcher to stop: the list is read once per
        // request and nothing is left running between reads.
        StopStreaming(wait: true, "Earshot is closing");
    }

    // The cursor as it is now, for a card that follows this click however long the action takes.
    private CardPlace ClickPlace() => CardPlace.AtClick(_cursorPosition());

    // viaHotkey: the trigger was a global shortcut, not the icon. It changes where the card that follows
    // lands (CardPlace.NearTray, since there is no click point) and whether a success is shown at all.
    private void StartToggle(bool viaHotkey = false)
    {
        CardPlace place = viaHotkey ? CardPlace.NearTray : ClickPlace();
        long clickedAt = _tickCount();
        Launch("toggle", () => ToggleAsync(place, clickedAt, viaHotkey), place);
    }

    private void Start(string action, Func<CardPlace, Task> work, bool viaHotkey = false)
    {
        CardPlace place = viaHotkey ? CardPlace.NearTray : ClickPlace();
        Launch(action, () => work(place), place);
    }

    // Starts an action from the icon or the menu. An action still running is kept so Exit can wait for
    // it, and an exception the action did not handle is logged and shown rather than lost with a
    // discarded task.
    private void Launch(string action, Func<Task> work, CardPlace place)
    {
        if (_closing)
        {
            _log.Info(action + ": ignored because Earshot is closing.");
            return;
        }

        Task task = RunReportedAsync(action, work, place);
        if (!task.IsCompleted)
        {
            _pending.Add(task, action);
            _ = ForgetWhenDoneAsync(task);
        }
    }

    private async Task RunReportedAsync(string action, Func<Task> work, CardPlace place)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, place);
        }
    }

    private async Task ForgetWhenDoneAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _pending.Remove(task);
        }
    }

    // Toggles the connection of the pinned (or resolved) device through the coordinator, which owns the
    // block, allow and protection order around it and shows its cards.
    private async Task ToggleAsync(CardPlace place, long clickedAt, bool viaHotkey = false)
    {
        if (_toggleInFlight)
        {
            if (_toggleSuperseded || clickedAt - _toggleClickedAt < (long)_doubleClickTime.TotalMilliseconds)
            {
                _log.Write(LogLevel.Debug, "Click ignored: a connect or disconnect is already in flight.");
                return;
            }

            await SupersedeToggleAsync(place, clickedAt, viaHotkey);
            return;
        }

        await RunToggleAsync(place, clickedAt, connect: null, viaHotkey);
    }

    // A click while a connect or disconnect is in flight asks for the opposite. The one in flight is cancelled and
    // the new one runs only once it has ended, clean-up included, so a re-block after a cancelled allow always
    // comes first. That can take minutes (a gate change already sent is waited for, a protect verb included), so the
    // click is answered with a card at once.
    private async Task SupersedeToggleAsync(CardPlace place, long clickedAt, bool viaHotkey = false)
    {
        bool connect = !_toggleConnect;
        string next = connect ? "connect" : "disconnect";

        // The disconnect in flight is left alone: the connect that would follow it is refused anyway.
        if (connect && RefusedAtSessionEnd(next, TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), place))
        {
            return;
        }

        TaskCompletionSource? done = _toggleDone;
        _toggleSuperseded = true;
        _toggleCancelReason = "a newer click asked to " + next;
        _log.Info("Click: the " + (connect ? "disconnect" : "connect") + " in flight is cancelled, and a " + next + " follows once it has finished.");
        ShowCard(TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), FinishingFirstMessage, place);
        _toggle?.Cancel();
        try
        {
            if (done is not null)
            {
                await done.Task;
            }
        }
        finally
        {
            _toggleSuperseded = false;
        }

        if (_closing)
        {
            _log.Info(next + ": not started, because Earshot is closing.");
            return;
        }

        await RunToggleAsync(place, clickedAt, connect, viaHotkey);
    }

    // connect: the intent a newer click asked for, or null to take it from the device state.
    private async Task RunToggleAsync(CardPlace place, long clickedAt, bool? connect, bool viaHotkey = false)
    {
        if (_toggleInFlight)
        {
            // Another click started one while this waited for the last to end.
            _log.Write(LogLevel.Debug, "Click ignored: a connect or disconnect is already in flight.");
            return;
        }

        EarshotSettings settings = _registry.Settings.Current;
        if (_operationsInFlight > 0)
        {
            // A menu action such as a protection change or a device change is still running through the
            // gate. A connect started now could allow the device nodes under it, so the click is not queued.
            _log.Info("Click ignored: a menu action is still in flight.");
            ShowCard(TrayStatus.DeviceName(_snapshot, settings), BusyMessage, place);
            return;
        }

        // The menu toggle is disabled while the device itself reports the link is changing, so a click is
        // ignored then too: the label and the click always agree.
        ConnectionState connection = TrayStatus.ActiveTarget(_snapshot, settings)?.Connection ?? ConnectionState.Unknown;
        if (connection is ConnectionState.Connecting or ConnectionState.Disconnecting)
        {
            _log.Write(LogLevel.Debug, "Click ignored: the link is already changing (" + connection + ").");
            return;
        }

        ToggleIntent? intent = TrayStatus.Intent(_snapshot, settings);
        if (intent is null)
        {
            string message = _snapshot.ReadStatus == SnapshotReadStatus.Failed
                ? ConnectMessages.CouldNotReadDevices
                : TrayStatus.NotFoundMessage(settings);
            _log.Warn("Toggle: " + message);
            ShowCard(settings.DeviceMatch, message, place);
            return;
        }

        bool wanted = connect ?? intent.Connect;

        // Before anything is claimed or shown: a refused connect raises no busy guard and says only why. The
        // coordinator would refuse it too, but only after the tray had answered "Finishing another change first."
        // to a click made while the session-end block runs. A disconnect still runs while the session ends (see
        // BlockCoordinator.ToggleAsync).
        if (wanted && RefusedAtSessionEnd("connect", intent.DeviceName, place))
        {
            return;
        }

        // Sleep has no session-ending flag of its own (it is not a session end), so a click reaching here while
        // the sleep hand-back holds the reply gets its own one-line card rather than "Finishing another change
        // first."; the coordinator's own queue (RunExclusiveAsync) still keeps the two from running at once.
        if (_coordinator.HandBackInProgress && !_coordinator.SessionEndInProgress)
        {
            _log.Info((wanted ? "connect" : "disconnect") + ": not started, because Earshot is handing the AirPods back.");
            ShowCard(intent.DeviceName, HandingBackMessage, place);
            return;
        }

        if (_coordinator.IsBusy)
        {
            // Work the coordinator started itself, which a connect or disconnect waits for.
            _log.Info("Click: the " + (wanted ? "connect" : "disconnect") + " waits for the block coordinator's own work to finish.");
            ShowCard(intent.DeviceName, FinishingFirstMessage, place);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _toggle = cts;
        _toggleInFlight = true;
        _toggleConnect = wanted;
        _toggleClickedAt = clickedAt;
        _toggleCancelReason = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _toggleDone = done;
        UpdatePresentation(forceIcon: false);
        string action = wanted ? "connect" : "disconnect";
        try
        {
            ToggleReport report = await _coordinator.ToggleAsync(
                new ToggleRequest(wanted, intent.Container, intent.DeviceName, place), cts.Token);

            // A controller that reports a cancellation rather than throwing one is still a cancelled click. The
            // coordinator names the reason when it cancelled the operation itself (a session end, say).
            bool cancelled = report.Cancelled || cts.IsCancellationRequested;
            string text = TrayReport.Describe(
                cancelled ? action + " (cancelled because " + (report.CancelledBecause ?? CancelReason()) + ")" : action,
                report.Status,
                report.UserMessage,
                report.Steps);
            _log.Write(report.IsSuccess || cancelled ? LogLevel.Info : LogLevel.Warn, text);

            // A click sees the result on the icon; a hotkey has nothing else to show it, so it gets a
            // card for the outcome, success included.
            if (viaHotkey && report.IsSuccess && !cancelled)
            {
                ShowCard(intent.DeviceName, report.UserMessage, place);
            }

            // VoiceOver: announce on the transition itself, never on a poll and never a state the app
            // only assumed, so a cancelled toggle says nothing. There is no VoiceLine for a failed
            // disconnect (the closed six-phrase set has no "disconnect failed" line), so that case is
            // left unspoken rather than widening the set past what the design's own acceptance tests
            // audited.
            if (!cancelled)
            {
                if (report.IsSuccess)
                {
                    AnnounceIfEnabled(wanted ? VoiceLine.Connected : VoiceLine.Disconnected);
                }
                else if (wanted)
                {
                    AnnounceIfEnabled(VoiceLine.ConnectFailed);
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _log.Info(action + ": cancelled because " + CancelReason() + ".");
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, place);
        }
        finally
        {
            _toggle = null;
            _toggleInFlight = false;
            _toggleDone = null;
            done.TrySetResult();
            UpdatePresentation(forceIcon: false);
            _ = _coordinator.RefreshStatusAsync();
        }
    }

    // True, with the card shown, when a session end is in progress, so the caller starts nothing: no connect, no
    // setting or device change, no setup. Every trigger (left click, menu, hotkey) reaches one of the callers, and
    // each asks before it saves a setting or claims the busy guard, so a refusal leaves nothing behind and the tray
    // is usable again as soon as Windows says the session end was cancelled. The coordinator refuses the same
    // operations itself, for one that was already waiting there when the session end began.
    private bool RefusedAtSessionEnd(string action, string title, CardPlace place)
    {
        if (!_coordinator.SessionEndInProgress)
        {
            return false;
        }

        _log.Info(action + ": not started, because the session is ending.");
        ShowCard(title, BlockCoordinator.SessionEndingMessage, place);
        return true;
    }

    // Why the tray cancelled the connect or disconnect in flight.
    private string CancelReason() =>
        _lifetime.IsCancellationRequested ? "Earshot is closing" : _toggleCancelReason ?? "another device was chosen";

    // The hidden window raises this for WM_QUERYENDSESSION and WM_ENDSESSION. Internal so a test can raise it
    // without a real end-session message.
    internal void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        SessionEnding?.Invoke(this, e);
        if (_closed)
        {
            return;
        }

        _coordinator.OnSessionEnding(e);

        // WM_ENDSESSION with the session really ending: the streaming connection is let go now, without waiting,
        // because this handler must return at once and how long the process lives afterwards is not documented.
        // A query is not acted on: the session end may still be cancelled, and the owner's music with it.
        if (!e.IsQuery && e.Ending)
        {
            StopStreaming(wait: false, "the session is ending");

            // Forgotten, not remembered as applied: should Windows abandon the shutdown after all and Earshot stay
            // open, the next settings change starts the feature again rather than finding nothing different.
            _streamingApplied = null;

            if (_registry.Settings.Current.HandBackOnShutdownAndSleep)
            {
                DateTimeOffset deadline = _time.GetUtcNow() + _handBackBudget;
                Task handBack = _coordinator.HandBackAsync(HandBackTrigger.SessionEnd, _handBackBudget, _disconnectHandBackWait);
                HoldReply(handBack, deadline);
            }
        }
    }

    // Pumps this thread's own message queue until task completes or deadline passes, so the caller (the real
    // window procedure, inside its own WndProc for WM_ENDSESSION or WM_POWERBROADCAST) does not return, and so
    // does not let the session end or the sleep transition proceed, until the hand-back has finished or run out
    // of its own budget. A blocking wait would deadlock: the coordinator's continuations are posted back to this
    // same thread. https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.doevents
    private void HoldReply(Task task, DateTimeOffset deadline)
    {
        DateTimeOffset started = _time.GetUtcNow();
        PumpUntilTaskOrDeadline(task, deadline, _time);
        _log.Write(LogLevel.Debug, "Hand-back: reply returned after " +
            ((long)(_time.GetUtcNow() - started).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms.");
    }

    // The pump itself, apart from the logging around it, so the real message-path test can drive the exact same
    // primitive against a real window procedure without building a whole TrayContext on a private desktop thread.
    internal static void PumpUntilTaskOrDeadline(Task task, DateTimeOffset deadline, TimeProvider time)
    {
        while (!task.IsCompleted && time.GetUtcNow() < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }

    // The hidden window raises this for WM_POWERBROADCAST. Internal so a test can raise it without a real
    // message. Suspend holds the reply the same way OnSessionEnding does; the resume kinds never hold anything
    // (sleep is not a session end, and nothing documented gives an application a budget to answer them in).
    internal void OnPowerChanged(object? sender, PowerEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        switch (e.Kind)
        {
            case PowerEventKind.Suspend:
                if (_registry.Settings.Current.HandBackOnShutdownAndSleep)
                {
                    // The same first step as the shut-down hand-back (3.2's step 2, "as 3.2" for sleep): a held
                    // streaming link is let go before the disconnect, not waited for on its own. Unlike a
                    // shutdown, sleep is never abandoned once it starts, so _streamingApplied is left as it is:
                    // the machine will resume with the tray still running, and a settings change that asks for
                    // nothing different must not restart a feature nothing actually stopped asking for.
                    StopStreaming(wait: false, "the machine is sleeping");

                    DateTimeOffset deadline = _time.GetUtcNow() + _sleepHandBackBudget;
                    Task handBack = _coordinator.HandBackAsync(HandBackTrigger.Suspend, _sleepHandBackBudget, _sleepDisconnectWait);
                    HoldReply(handBack, deadline);
                }
                else
                {
                    _log.Info("Hand-back: off, so nothing runs for this suspend.");
                }

                break;

            case PowerEventKind.ResumeAutomatic:
                // Not held: resume is not a race against a Windows-imposed budget, and _handingBack has already
                // cleared by the time this arrives (the suspend handler above has returned).
                _ = _coordinator.ResumeCheckAsync();
                break;

            case PowerEventKind.ResumeSuspend:
                _log.Write(LogLevel.Debug, "WM_POWERBROADCAST: PBT_APMRESUMESUSPEND (logged only).");
                break;
        }
    }

    // WM_QUERYENDSESSION and WM_ENDSESSION, for anything else that wants them; the coordinator is wired
    // above and starts the best-effort block itself.
    public event EventHandler<SessionEndingEventArgs>? SessionEnding;

    private void OnSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        if (_closing || _closed)
        {
            return;
        }

        _snapshot = e.Snapshot;
        UpdatePresentation(forceIcon: false);
        _ = PinIfFirstSightingAsync();
    }

    // Settings.Update raises Changed before it returns, so the presentation is refreshed afterwards.
    // Hotkeys are re-applied here too, on the window thread, so a shortcut typed and saved takes effect
    // at once and one no longer wanted is released.
    private void OnSettingsChanged(object? sender, EarshotSettings e) =>
        _registry.UiPost(() =>
        {
            if (!_closed)
            {
                UpdatePresentation(forceIcon: false);
                ApplyHotkeys();
                ApplyVoiceOver();
                ApplyStreaming();
            }
        });

    // Registers what the current settings ask for. Every outcome is logged, every time. A failure for one shortcut
    // never stops the others, and nothing here decides who else holds a combination beyond the one code (1409) the
    // platform documents as meaning that. If any shortcut did not take (held by another program, rejected text, a
    // Windows error), the owner sees one card, not one per shortcut, naming the first that failed and why. Every
    // settings save comes through here, so the card is shown only when what is wrong has changed since the last
    // time: a shortcut that keeps failing the same way is not reported again on each unrelated save.
    private void ApplyHotkeys()
    {
        if (_closed)
        {
            return;
        }

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = _hotkeys.Apply(_registry.Settings.Current.Hotkeys);
        var problems = new List<HotkeyRegistrationOutcome>();
        foreach (HotkeyRegistrationOutcome outcome in outcomes)
        {
            if (outcome.State is HotkeyRegistrationState.NotSet or HotkeyRegistrationState.Registered)
            {
                _log.Write(LogLevel.Debug, "Hotkey " + outcome.Action + ": " + outcome.Message);
            }
            else
            {
                _log.Warn("Hotkey " + outcome.Action + ": " + outcome.Message);
                problems.Add(outcome);
            }
        }

        string signature = string.Join("\n", problems.Select(p => p.Action + "|" + p.State + "|" + p.ErrorCode.ToString(CultureInfo.InvariantCulture) + "|" + p.RequestedText));
        if (signature == _hotkeyProblemsShown)
        {
            return;
        }

        _hotkeyProblemsShown = signature;
        if (problems.Count > 0)
        {
            ShowCard(TrayStatus.AppName, HotkeyProblemCard(problems), CardPlace.NearTray);
        }
    }

    // The first shortcut that was not set and why, in the outcome's own words, which name the shortcut except for
    // rejected text; that is quoted (cut short, since it is whatever was typed). More than one: the log has the rest.
    internal static string HotkeyProblemCard(IReadOnlyList<HotkeyRegistrationOutcome> problems)
    {
        HotkeyRegistrationOutcome first = problems[0];
        string reason = first.Message;
        if (first.State == HotkeyRegistrationState.TextRejected)
        {
            string typed = first.RequestedText.Length > 24 ? first.RequestedText[..24] + "..." : first.RequestedText;
            reason = "\"" + typed + "\" is not a shortcut. " + reason;
        }

        // The text is whatever the settings file held, and the outcome's message may quote it: no control character
        // (a line break, a bell, an escape sequence) goes on to the card.
        reason = string.Concat(reason.Where(c => !char.IsControl(c)));
        return problems.Count == 1 ? reason : reason + HotkeyOthersNotSetSuffix;
    }

    // The menu tick toggles the owner's VoiceOver.Enabled setting; ApplyVoiceOver (raised through
    // Settings.Changed, the same path ApplyHotkeys already uses) is what actually starts or stops the
    // announcer, so there is exactly one place that opens or closes the speech engine.
    private void OnSpeakStatusClicked()
    {
        if (_closing)
        {
            return;
        }

        CardPlace place = ClickPlace();
        bool enable = !_registry.Settings.Current.VoiceOver.Enabled;
        if (!TryUpdateSettings("speak status", s => s.VoiceOver = s.VoiceOver with { Enabled = enable }, place))
        {
            return;
        }

        ShowCard(TrayStatus.AppName, enable ? AnnouncerCopy.SpeechOn : AnnouncerCopy.SpeechOff, place);
    }

    // Read-only: reads the state the tray already holds (the same snapshot and settings the menu label
    // and the left click read) and speaks it, and never itself triggers a status probe, a connect or a
    // node change. If VoiceOver is off or unavailable there is nothing to speak, and this does nothing
    // else either: it must never fall back to some other action.
    //
    // Speaks Connected or Disconnected only when the snapshot says exactly that: Connecting,
    // Disconnecting and Unknown are states the app only assumed (a read in flight, or no read at all),
    // and the voiceover design (section 9) says never to announce one of those. The closed six-phrase
    // set has no line for any of them, so widening it is not an option either. What the owner asked to
    // hear about is shown instead, as the same short line the tray's own tooltip would show, and the log
    // records why nothing was spoken.
    private void SpeakCurrentStatus()
    {
        if (_voice is not { IsAvailable: true } voice)
        {
            return;
        }

        ConnectionState connection = TrayStatus.ActiveTarget(_snapshot, _registry.Settings.Current)?.Connection ?? ConnectionState.Unknown;
        VoiceLine? line = connection switch
        {
            ConnectionState.Connected => VoiceLine.Connected,
            ConnectionState.Disconnected => VoiceLine.Disconnected,
            _ => null,
        };

        if (line is not { } toSpeak)
        {
            _log.Write(LogLevel.Debug, "VoiceOver speak status: nothing spoken, state is " + connection + ".");
            ShowCard(TrayStatus.AppName, TrayStatus.Tooltip(_snapshot, BlockStatus, _registry.Settings.Current), CardPlace.NearTray);
            // Drains and logs exactly like the spoken branch below: an outcome the worker recorded since
            // the last drain (a repeat refused inside the gap, a speak failure) must not sit unlogged
            // just because this particular call had nothing new to speak.
            LogVoiceOutcomes(voice.Drain());
            return;
        }

        voice.Announce(toSpeak);
        LogVoiceOutcomes(voice.Drain());
    }

    // Announces a state transition the coordinator or this class already observed (never a poll, and
    // never a state the app only assumed): a connect or disconnect that finished, or a Block at boot
    // change that finished. Does nothing when VoiceOver is off or unavailable.
    private void AnnounceIfEnabled(VoiceLine line)
    {
        if (_voice is not { IsAvailable: true } voice)
        {
            return;
        }

        voice.Announce(line);
        LogVoiceOutcomes(voice.Drain());
    }

    // Opens or closes the speech engine to match what settings.VoiceOver (clamped) now asks for, and does
    // nothing when a settings change asked for nothing different (comparing the clamped record, the same
    // way ApplyHotkeys is re-run on every settings change but only changes what actually differs). Called
    // from the constructor and from OnSettingsChanged, on the UI thread only, the same thread Start and
    // Stop must run on.
    private void ApplyVoiceOver()
    {
        if (_closed)
        {
            return;
        }

        VoiceOverSettings clamped = _registry.Settings.Current.VoiceOver.Clamped(out IReadOnlyList<StepOutcome> notes);
        foreach (StepOutcome note in notes)
        {
            _log.Write(LogLevel.Debug, "VoiceOver " + note.Step + ": " + note.Detail);
        }

        if (clamped == _voiceApplied)
        {
            return;
        }

        _voiceApplied = clamped;
        StopVoice();

        if (!clamped.Enabled)
        {
            return;
        }

        var voice = new SpeechAnnouncer(_voiceEngineFactory(), clamped, TimeProvider.System);
        voice.Stopped += OnVoiceStopped;
        StepOutcome outcome = voice.Start();
        LogVoiceOutcomes(voice.Drain());

        if (outcome.Ok)
        {
            _voice = voice;
            return;
        }

        _log.Warn("VoiceOver open: " + outcome.CodeName + " (" + outcome.Code.ToString(CultureInfo.InvariantCulture) + ") " + outcome.Detail);
        voice.Stopped -= OnVoiceStopped;
        voice.Dispose();

        if (outcome.Code == NativeCodes.NotAvailable)
        {
            // Not a failure of anything: GetInstalledVoices found no usable voice. The menu shows the
            // "(no voice)" label and refuses the click from now on (_voiceKnownNoVoice), and the owner's
            // Enabled choice is left as it is: installing a voice and restarting Earshot should resume
            // speaking on its own.
            _voiceKnownNoVoice = true;
            ShowCard(TrayStatus.AppName, AnnouncerCopy.NoVoiceInstalled, CardPlace.NearTray);
            return;
        }

        // The engine threw opening: this is a real failure, not "no voice is installed", so the setting
        // itself is turned back off, matching AnnouncerCopy.SpeechStopped exactly.
        ShowCard(TrayStatus.AppName, AnnouncerCopy.SpeechStopped, CardPlace.NearTray);
        TurnVoiceOverOff();
    }

    // The worker gave up on its own after FailuresBeforeGivingUp consecutive Speak failures (never for an
    // explicit Stop). Posted to the UI thread because SpeechAnnouncer raises this from its own worker
    // thread (see IAnnouncer.Stopped). Internal so a test can raise it directly, the same seam
    // OnHotkeyActivated and OnSessionEnding already are.
    //
    // sender is the announcer that gave up. By the time the posted action runs, a fast settings change
    // (or another give-up) could already have replaced _voice with a new announcer: this must not act on
    // that stale event, or it would stop and disable the current, working announcer over a failure that
    // belonged to the one it already replaced.
    internal void OnVoiceStopped(object? sender, StepOutcome outcome) =>
        _registry.UiPost(() =>
        {
            if (_closed || !ReferenceEquals(sender, _voice))
            {
                return;
            }

            _log.Warn("VoiceOver speak: " + outcome.CodeName + " (" + outcome.Code.ToString(CultureInfo.InvariantCulture) + ") " +
                outcome.Detail + " Speech has turned itself off after repeated failures.");
            ShowCard(TrayStatus.AppName, AnnouncerCopy.SpeechStopped, CardPlace.NearTray);
            TurnVoiceOverOff();
        });

    // Stops and disposes whatever announcer is running, and writes the owner's setting back to off, so
    // the menu tick and the persisted setting both agree that speech is off. Used for the two failures
    // section 4 of the voiceover design says end with "Earshot has turned it off": the engine throwing on
    // open, and repeated Speak failures.
    private void TurnVoiceOverOff()
    {
        StopVoice();
        if (_registry.Settings.Current.VoiceOver.Enabled)
        {
            TryUpdateSettings("voiceover (turned off)", s => s.VoiceOver = s.VoiceOver with { Enabled = false }, CardPlace.NearTray);
        }
    }

    // Stops, drains and disposes whatever announcer is running. Every outcome StopSpeaking and Dispose
    // recorded (including a timed-out "worker still speaking", and any Speak failure that had not yet
    // been drained because it landed on the worker thread after the last Announce) is logged here rather
    // than discarded: this used to be the one place a recorded outcome could go missing, since nothing
    // else ever calls Drain() once the announcer is on its way out.
    private void StopVoice()
    {
        if (_voice is null)
        {
            return;
        }

        SpeechAnnouncer voice = _voice;
        _voice = null;
        voice.Stopped -= OnVoiceStopped;
        voice.StopSpeaking();
        LogVoiceOutcomes(voice.Drain());
        voice.Dispose();
        LogVoiceOutcomes(voice.Drain());
    }

    private void LogVoiceOutcomes(IReadOnlyList<StepOutcome> outcomes)
    {
        foreach (StepOutcome outcome in outcomes)
        {
            _log.Write(outcome.Ok ? LogLevel.Debug : LogLevel.Warn,
                "VoiceOver " + outcome.Step + ": " + outcome.CodeName + " (" + outcome.Code.ToString(CultureInfo.InvariantCulture) + ") " + (outcome.Detail ?? ""));
        }
    }

    // Starts or stops Play from a phone to match what settings.Streaming (clamped) now asks for, and does nothing
    // when a settings change asked for nothing different. Called from the constructor and from OnSettingsChanged, on
    // the UI thread only. Off is the default, and while it is off no platform is built, no WinRT type is touched,
    // nothing is enumerated and nothing listens.
    private void ApplyStreaming()
    {
        if (_closed || _closing)
        {
            return;
        }

        StreamingSettings current = _registry.Settings.Current.Streaming;
        StreamingSettings wanted = current.Clamped(out IReadOnlyList<StepOutcome> notes);
        if (wanted == _streamingApplied)
        {
            return;
        }

        foreach (StepOutcome note in notes)
        {
            _log.Warn("Play from a phone " + note.Step + ": " + note.Detail);
        }

        // Nothing new starts while a session end is in progress. It is not written down as applied, so the next
        // settings change, or the next start of Earshot, takes it up. Turning the feature off is always honoured.
        if (wanted.Enabled && _coordinator.SessionEndInProgress)
        {
            _log.Info("Play from a phone: not started, because the session is ending.");
            return;
        }

        _streamingApplied = wanted;
        StopStreaming(wait: false, "the setting changed");
        if (!wanted.Enabled)
        {
            return;
        }

        int generation = _streamingGeneration;
        Launch("play from a phone (switch on)", () => StartStreamingAsync(wanted, generation), CardPlace.NearTray);
    }

    // The support check is a call into WinRT, so the coordinator is built on a pool thread and taken up here, on the
    // UI thread, only if it is still wanted: not once Earshot is closing, and not once a later settings change or a
    // session end has moved on.
    private async Task StartStreamingAsync(StreamingSettings settings, int generation)
    {
        IStreamingPlatform platform = _streamingPlatformFactory(_log);
        if (_registry.SafeMode)
        {
            platform = SafeStreamingPlatform.Wrap(platform, _log);
        }

        StreamingCoordinator streaming = await Task.Run(() => new StreamingCoordinator(platform, _streamingGate, settings, IsManagedDevice, TimeProvider.System, _log));
        if (_closed || _closing || generation != _streamingGeneration)
        {
            _log.Info("Play from a phone: no longer wanted by the time it was ready, so it was not started.");
            await Task.Run(streaming.Dispose);
            return;
        }

        _streaming = streaming;
        streaming.Changed += OnStreamingChanged;
        if (streaming.Support != StreamingSupport.Supported)
        {
            // A disabled menu item cannot open to show why, so the reason goes on one card, once per switch-on.
            _log.Warn("Play from a phone: " + streaming.Support + ". " + StreamingLog.Describe(streaming.SupportStep));
            ShowCard(TrayStatus.AppName, StreamingCopy.ForSupport(streaming.Support), CardPlace.NearTray);
            UpdatePresentation(forceIcon: false);
            return;
        }

        _log.Info("Play from a phone is on. Nothing is enabled or opened until a device is chosen from the menu.");
        await RefreshStreamingAsync(streaming, clicked: false, CardPlace.NearTray);
    }

    // The device Earshot manages is never offered, enabled or opened by Play from a phone. Asked by the coordinator,
    // from whatever thread reads the list; Settings.Current is safe to read from any thread.
    private bool IsManagedDevice(StreamingDevice device)
    {
        EarshotSettings settings = _registry.Settings.Current;
        bool managed = StreamingExclusion.IsManagedDevice(device, settings.PinnedContainerId, settings.DeviceMatch);
        if (managed)
        {
            _log.Write(LogLevel.Debug, "Play from a phone: device " + StreamingLog.Key(device.DeviceId) + " is the one Earshot manages, so it is not offered.");
        }

        return managed;
    }

    // Lets go of everything Play from a phone holds. Closing waits for it, up to StreamingShutdownWait; a settings
    // change and a session end do not wait, so the UI thread is never held for them. The coordinator writes the
    // outcome of every release to the log itself, with its raw code, as each one happens; what is left to the tray is
    // to tell the owner, once, when Windows did not confirm one.
    private void StopStreaming(bool wait, string why)
    {
        // A start still building its coordinator sees this and disposes what it built.
        _streamingGeneration++;

        StreamingCoordinator? streaming = _streaming;
        if (streaming is not null)
        {
            _streaming = null;
            streaming.Changed -= OnStreamingChanged;

            // Letting go itself is asynchronous, so without this the tooltip and icon could keep reading the
            // connection just switched away from (e.g. "Waiting for <phone>...") until something unrelated
            // refreshed them next. A no-op once Earshot is closed: UpdatePresentation guards that itself.
            UpdatePresentation(forceIcon: false);
        }

        if (!wait)
        {
            if (streaming is null)
            {
                return;
            }

            _log.Info("Play from a phone: letting go of any connection, because " + why + ".");

            // Started now whatever else is going on, Exit included, and kept with the other actions in flight so Exit
            // waits for it. Not through Launch, which starts nothing once Earshot is closing: this must always run.
            Task letGo = LetGoOfStreamingAsync(streaming);
            if (!letGo.IsCompleted)
            {
                _pending.Add(letGo, "play from a phone (let go)");
                _ = ForgetWhenDoneAsync(letGo);
            }

            return;
        }

        // Final Close only: the current connection, if there is one, and every connection an earlier switch-off
        // could not get Windows to confirm releasing, tried again once through the same platform instance that
        // still holds it: a fresh one would have nothing to release. All of it inside the one closing budget
        // below, not a fresh one for each: StreamingShutdownWait is not extended by how many are waiting for it.
        var toRelease = new List<StreamingCoordinator>(_unconfirmedStreaming);
        _unconfirmedStreaming.Clear();
        if (streaming is not null)
        {
            toRelease.Add(streaming);
        }

        if (toRelease.Count == 0)
        {
            return;
        }

        _log.Info("Play from a phone: letting go of any connection, because " + why + ".");
        Task<List<StreamingCoordinator>> release = Task.Run(() => LetGoOfEachStreaming(toRelease));
        try
        {
            if (!release.Wait(_streamingShutdownWait))
            {
                _log.Warn("Play from a phone: Windows had not let go of the connection after " + Seconds(_streamingShutdownWait) +
                    ". Earshot closes without waiting longer; the references end with the process.");
                _ = ObserveStreamingReleaseAsync(release);
            }
        }
        catch (AggregateException ex)
        {
            Exception cause = ex.GetBaseException();
            _log.Error("Play from a phone: letting go of the connection failed (" + NativeCodes.Name(cause.HResult) + ", " + cause.GetType().Name + ").", cause);
        }
    }

    // Releases everything the coordinator holds. If Windows confirmed releasing all of it, the coordinator is
    // disposed and this returns null. If something is still unconfirmed, the coordinator is returned instead of
    // disposed: it is the platform instance that still holds the live connection, and a fresh coordinator built
    // later would hold nothing to release. The caller decides what to do with what comes back.
    private static StreamingCoordinator? LetGoOfEverything(StreamingCoordinator streaming)
    {
        IReadOnlyList<StreamingReleaseOutcome> outcomes = streaming.ReleaseAll();
        if (outcomes.Any(o => o.Failed))
        {
            return streaming;
        }

        streaming.Dispose();
        return null;
    }

    // The Close-time retry: every coordinator handed to it gets one more attempt, through its own platform
    // instance, and whatever is still unconfirmed after that comes back so the caller can decide whether to wait
    // any longer for it. Static: touches nothing on the tray, so it can run on a pool thread.
    private static List<StreamingCoordinator> LetGoOfEachStreaming(IReadOnlyList<StreamingCoordinator> coordinators)
    {
        var stillUnconfirmed = new List<StreamingCoordinator>();
        foreach (StreamingCoordinator streaming in coordinators)
        {
            if (LetGoOfEverything(streaming) is { } unconfirmed)
            {
                stillUnconfirmed.Add(unconfirmed);
            }
        }

        return stillUnconfirmed;
    }

    private async Task LetGoOfStreamingAsync(StreamingCoordinator streaming)
    {
        StreamingCoordinator? unconfirmed = streaming;
        try
        {
            unconfirmed = await Task.Run(() => LetGoOfEverything(streaming));
        }
        catch (Exception ex)
        {
            _log.Error("Play from a phone: letting go of the connection failed (" + NativeCodes.Name(ex.HResult) + ", " + ex.GetType().Name + ").", ex);
        }

        if (unconfirmed is not null)
        {
            // Not lost with this coordinator: Windows has not confirmed releasing it, and Close makes the one
            // further attempt this deserves, through the same instance, once, not one on every switch back on
            // before then.
            _unconfirmedStreaming.Add(unconfirmed);
        }

        ShowStreamingReleaseFailure(streaming, CardPlace.NearTray);
    }

    private async Task ObserveStreamingReleaseAsync(Task release)
    {
        try
        {
            await release.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Play from a phone: letting go of the connection failed (" + NativeCodes.Name(ex.HResult) + ", " + ex.GetType().Name + ").", ex);
        }
    }

    // When Windows did not confirm a release, the owner is told once, in the coordinator's own words, that the phone
    // may still be connected to this PC. Asked for at the end of everything the tray starts on the coordinator, so
    // whichever step the release failed in, it is said, and said in place of whatever else that step had to say. The
    // code behind it is already in the log. No card is shown once Earshot is closing (ShowCard); the log still has it.
    private bool ShowStreamingReleaseFailure(StreamingCoordinator streaming, CardPlace place)
    {
        if (streaming.TakeReleaseFailureNotice() is not { } notice)
        {
            return false;
        }

        ShowCard(TrayStatus.AppName, notice, place);
        return true;
    }

    // The coordinator raises this from a pool thread or from the thread Windows reports a link change on.
    private void OnStreamingChanged(object? sender, EventArgs e) =>
        _registry.UiPost(() =>
        {
            if (!_closed && ReferenceEquals(sender, _streaming))
            {
                UpdatePresentation(forceIcon: false);
            }
        });

    // The right button is about to open the menu, so the list is read again for it. Opening the menu itself still
    // reads nothing (TrayMenu.OnOpening). The left button is the AirPods toggle and reads no list.
    // Internal so a test can press a button without an icon on screen, as with OnIconMouseClick.
    internal void OnIconMouseDownForStreaming(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && !_closing && _streaming is { Support: StreamingSupport.Supported } streaming)
        {
            Launch("play from a phone (list)", () => RefreshStreamingAsync(streaming, clicked: false, CardPlace.NearTray), CardPlace.NearTray);
        }
    }

    // A click in the Play from a phone submenu. Internal so a test can raise one without opening the menu.
    internal void OnPlayFromPhoneItemClicked(object? sender, StreamingMenuItemEventArgs e)
    {
        if (_closing || _streaming is not { } streaming)
        {
            return;
        }

        CardPlace place = ClickPlace();
        StreamingMenuItem item = e.Item;
        switch (item.Command)
        {
            case StreamingMenuCommand.Play when item.DeviceId is { } deviceId:
                Launch("play from a phone", () => StartPlayingAsync(streaming, deviceId, place), place);
                break;

            case StreamingMenuCommand.Stop:
                Launch("play from a phone (stop)", () => StopPlayingAsync(streaming, item.DeviceId, place), place);
                break;

            case StreamingMenuCommand.Refresh:
                Launch("play from a phone (list)", () => RefreshStreamingAsync(streaming, clicked: true, place), place);
                break;

            default:
                _log.Write(LogLevel.Debug, "Play from a phone: a click on a sentence does nothing.");
                break;
        }
    }

    // Reads the list of paired devices that can send audio to this PC. A read of the device store: it scans for
    // nothing and connects to nothing. clicked: the owner asked for it, so a refusal or a failure gets a card; a read
    // Earshot started itself only logs, and the menu says what happened.
    private async Task RefreshStreamingAsync(StreamingCoordinator streaming, bool clicked, CardPlace place)
    {
        if (_coordinator.SessionEndInProgress)
        {
            if (clicked)
            {
                RefusedAtSessionEnd("play from a phone (list)", TrayStatus.AppName, place);
            }
            else
            {
                _log.Info("Play from a phone (list): not read, because the session is ending.");
            }

            return;
        }

        if (_streamingRefreshInFlight)
        {
            _log.Write(LogLevel.Debug, "Play from a phone (list): a read is already in flight.");
            return;
        }

        _streamingRefreshInFlight = true;
        try
        {
            StreamingDiscovery discovery = await Task.Run(() => streaming.RefreshAsync(_lifetime.Token));
            if (_closed || !ReferenceEquals(streaming, _streaming))
            {
                return;
            }

            if (discovery.Status == StreamingDiscoveryStatus.Ok)
            {
                _log.Info("Play from a phone (list): " + discovery.Devices.Count.ToString(CultureInfo.InvariantCulture) + " device(s) can send audio to this PC.");
            }
            else
            {
                _log.Warn("Play from a phone (list): " + discovery.Status + ". " + StreamingLog.Describe(discovery.Step));
                if (clicked)
                {
                    ShowCard(TrayStatus.AppName, StreamingCopy.CouldNotReadList, place);
                }
            }

            // A good read lets go of a device in use that it no longer holds; if Windows did not confirm that, say so.
            ShowStreamingReleaseFailure(streaming, place);
            UpdatePresentation(forceIcon: false);
        }
        finally
        {
            _streamingRefreshInFlight = false;
        }
    }

    // Starts playing from one device: enable, then open, through the coordinator. Refused with a card, before
    // anything is asked of Windows, while a session end is in progress, and while, at the moment of the click and as
    // read here on the UI thread, the tray has a connect, a disconnect or a menu action in flight or the block
    // coordinator has an operation in flight. One start at a time: a second click while one is in flight is answered,
    // not queued.
    //
    // That is all that is guaranteed, and it runs one way only. Nothing holds the AirPods side back once a start is
    // under way: the owner's connect and disconnect are never held up by this, on purpose, and the block
    // coordinator's own work (the check at start-up, an automatic block) can begin at any time, so either may run
    // while Windows is still enabling or opening the phone's connection. What one radio does when the two overlap is
    // not documented anywhere and is a live-test item; nothing here claims they cannot meet.
    //
    // Safe mode is not checked here. It is enforced in one place, at the bottom: StartStreamingAsync wraps the platform
    // in SafeStreamingPlatform, which refuses to enable or open whoever asks, the way the registry wraps the
    // controllers. The refusal comes back on the outcome and is what the card says.
    private async Task StartPlayingAsync(StreamingCoordinator streaming, string deviceId, CardPlace place)
    {
        if (RefusedAtSessionEnd("play from a phone", TrayStatus.AppName, place))
        {
            return;
        }

        if (IsBusy || _coordinator.IsBusy || _streamingStartInFlight)
        {
            _log.Info("Play from a phone: not started, because another change is still running.");
            ShowCard(TrayStatus.AppName, StreamingCopy.BusyJustNow, place);
            return;
        }

        // The coordinator reads the busy state again from a pool thread, through the gate. It is written here from
        // the check just made, so the two agree at the moment of the click whatever was last presented. By the time
        // the pool thread reads it, it is a moment old: a second look at the same answer, not a lock.
        _streamingGate.Set(false);
        _streamingStartInFlight = true;
        try
        {
            StreamingOpenOutcome outcome = await Task.Run(() => streaming.StartPlayingAsync(deviceId, _lifetime.Token));
            bool open = outcome.Status == StreamingOpenStatus.Open;
            _log.Write(
                open ? LogLevel.Info : LogLevel.Warn,
                "Play from a phone: device " + StreamingLog.Key(deviceId) + ": " + outcome.Status + ". " + StreamingLog.Describe(outcome.Step));
            if (_closed)
            {
                return;
            }

            // A release Windows did not confirm is what the owner most needs to know, so it takes the card. Otherwise
            // every outcome with something to tell the owner has left its sentence on the status line. A start that
            // never began has none, unless it was the busy gate that stopped it.
            if (ShowStreamingReleaseFailure(streaming, place))
            {
                // Said. The reason the start failed is in the log, a line above the release that failed.
            }
            else if (outcome.Step.Detail == SafeDecorators.Message)
            {
                ShowCard(TrayStatus.AppName, SafeDecorators.Message, place);
            }
            else if (outcome.Status != StreamingOpenStatus.NotStarted)
            {
                ShowCard(TrayStatus.AppName, streaming.StatusLine, place);
            }
            else if (outcome.Step.Detail == HostBusyGate.Reason)
            {
                ShowCard(TrayStatus.AppName, StreamingCopy.BusyJustNow, place);
            }

            UpdatePresentation(forceIcon: false);
        }
        finally
        {
            _streamingStartInFlight = false;
        }
    }

    // Stops accepting audio from one device: the one in use, or one Windows has not yet confirmed letting go of, which
    // keeps its Stop item so that the owner can try again. Always allowed, a session end included: it only lets go.
    // The card says this PC stopped accepting audio only when Windows confirmed it.
    private async Task StopPlayingAsync(StreamingCoordinator streaming, string? deviceId, CardPlace place)
    {
        StreamingReleaseOutcome outcome = await Task.Run(() => streaming.StopPlaying(deviceId));
        if (_closed)
        {
            return;
        }

        if (!ShowStreamingReleaseFailure(streaming, place) && outcome.Released)
        {
            ShowCard(TrayStatus.AppName, streaming.StatusLine, place);
        }

        UpdatePresentation(forceIcon: false);
    }

    // The global shortcut fired: hand it to the exact method the matching menu item or the left click
    // already uses, so a hotkey never opens a second route to the device. SpeakStatus is the one
    // exception to "opens a route to the device": it reads the state the tray already holds and speaks
    // it, and never itself starts a connect, disconnect or gate change (SpeakCurrentStatus). Internal so
    // tests can raise each action without registering a real global hotkey, the same way OnIconMouseClick
    // and OnSessionEnding already are.
    internal void OnHotkeyActivated(object? sender, HotkeyActivatedEventArgs e)
    {
        if (_closing || _closed)
        {
            return;
        }

        switch (e.Action)
        {
            case HotkeyAction.ToggleConnection:
                _log.Info("Hotkey: toggle connection.");
                StartToggle(viaHotkey: true);
                break;

            case HotkeyAction.ToggleAudioProtection:
                _log.Info("Hotkey: toggle audio protection.");
                OnProtectAudioClicked(viaHotkey: true);
                break;

            case HotkeyAction.ToggleBlockAtBoot:
                _log.Info("Hotkey: toggle block at boot.");
                Start("block at boot", place => BlockAtBootAsync(place, viaHotkey: true), viaHotkey: true);
                break;

            case HotkeyAction.SpeakStatus:
                _log.Info("Hotkey: speak status.");
                SpeakCurrentStatus();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.Action, "Unknown hotkey action.");
        }
    }

    private void OnCoordinatorChanged(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        UpdatePresentation(forceIcon: false);

        // Once Exit is chosen, the card for the block before closing goes up only when that block is sent.
        if (_exitPlace is { } place && !_blockingCardShown && _coordinator.BlockingBeforeClosing)
        {
            _blockingCardShown = true;
            place.Show(_registry.Cards, TrayStatus.AppName, BlockingBeforeClosingMessage);
        }
    }

    private MenuState CurrentMenuState() =>
        MenuModel.Build(_snapshot, BlockStatus, _coordinator.ProtectionStatus, _registry.Settings.Current, IsBusy || _coordinator.IsBusy, _startupState, _registry.SafeMode, _voiceKnownNoVoice, _streaming?.Menu);

    private void UpdatePresentation(bool forceIcon)
    {
        if (_closed)
        {
            return;
        }

        RefreshIcon(remeasure: forceIcon, force: forceIcon);

        // The tray's own busy flags change only beside a call to this method, and the block coordinator's Changed
        // event ends here too, so this is where the copy of the busy state that the streaming coordinator reads is
        // refreshed. It is a copy: written here on the UI thread, read later on a pool thread, and so possibly a
        // moment old when read. StartPlayingAsync explains what that does and does not guarantee. The streaming line
        // is added only while a device is in use or may still be connected; at rest the tooltip is what it always was.
        _streamingGate.Set(IsBusy || _coordinator.IsBusy);
        _notifyIcon.Text = StreamingCopy.TooltipWith(
            TrayStatus.Tooltip(_snapshot, BlockStatus, _registry.Settings.Current),
            _streaming?.TooltipLine ?? "",
            TrayStatus.MaxTooltipLength);
    }

    private void RefreshIcon(bool remeasure, bool force)
    {
        if (_closed)
        {
            return;
        }

        GlyphState glyph = TrayStatus.Glyph(_snapshot, BlockStatus, _registry.Settings.Current, _toggleInFlight);
        _icons.Apply(_notifyIcon, glyph, remeasure, force);
    }

    private async Task RunSetupAsync(CardPlace place)
    {
        ControllerResult? result = await RunOperationAsync("setup", ct => _coordinator.RunAsync("setup", _registry.Block.RunSetupAsync, ct), place);
        if (result is { IsSuccess: true } && !_closing)
        {
            PointStartupAtInstalledCopy();
        }
    }

    // Setup has put a copy in %ProgramFiles%\Earshot: with Open on startup on, the Run value starts that copy from now
    // on (StartupRegistration.TargetExePath), so deleting the download folder does not stop Earshot opening at
    // sign-in. This copy keeps running until Earshot is closed.
    private void PointStartupAtInstalledCopy()
    {
        if (!_registry.Settings.Current.OpenOnStartup || !_startup.RunValueNeedsRepair())
        {
            return;
        }

        if (_startup.WritesBlocked)
        {
            _log.Info(_startup.BlockedMessage + " Open on startup was not pointed at " + _startup.TargetExePath + " after setup.");
            return;
        }

        _log.Info("Setup is done, so Open on startup now starts " + _startup.TargetExePath + ".");
        Report("open-on-startup (after setup)", _startup.Apply(true), CardPlace.NearTray);
        _startupState = _startup.Read();
    }

    // The menu item and the hotkey both call this one method, so there is one route and one busy guard for
    // both. The guard is claimed synchronously, before the first await, so a burst of calls (a key held
    // down, or several hotkey presses queued back to back) lets only the first one through: every later
    // one in the same burst sees _operationsInFlight already raised and is refused at once, rather than
    // each starting its own read and queuing its own gate change.
    //
    // A hotkey may turn Block at boot on, but never off: turning off the boot protection is a deliberate
    // act the owner should see happening, which a card confirms but does not itself provide the assurance
    // the menu does (a checked box the owner chose to uncheck), so it is refused and pointed at the menu.
    private async Task BlockAtBootAsync(CardPlace place, bool viaHotkey = false)
    {
        // Asked here as well as in RunOperationAsync, which would come too late: with no status cached this method
        // first raises the busy guard and starts a status read on the system worker, and a read that fails ends in
        // BlockStatusUnreadableMessage without ever reaching RunOperationAsync. While the session ends none of that
        // happens, and the owner is told why nothing was changed.
        if (RefusedAtSessionEnd("block at boot", TrayStatus.AppName, place))
        {
            return;
        }

        if (IsBusy || _coordinator.IsBusy)
        {
            ShowCard(TrayStatus.AppName, BusyMessage, place);
            return;
        }

        _operationsInFlight++;
        UpdatePresentation(forceIcon: false);
        try
        {
            // The cached status is read again whenever the icon is pressed, which happens before the menu
            // opens; it is read here only when nothing has been read yet.
            BootBlockStatus? status = BlockStatus ?? await _coordinator.ReadBlockStatusAsync(_lifetime.Token);
            if (_closing)
            {
                return;
            }

            if (status is null)
            {
                ShowCard(TrayStatus.AppName, BlockStatusUnreadableMessage, place);
                return;
            }

            // Before setup there is no gate to take the setting. The click runs setup instead; a hotkey
            // never starts setup on its own, because that ends in an elevation prompt the owner did not
            // visibly ask for.
            if (TrayStatus.NeedsSetUp(status))
            {
                if (viaHotkey)
                {
                    ShowCard(TrayStatus.AppName, HotkeySetupNeededMessage, place);
                    return;
                }

                await RunSetupAsync(place);
                return;
            }

            bool blockAtBoot = !status.BlockAtBoot;
            if (viaHotkey && !blockAtBoot)
            {
                ShowCard(TrayStatus.AppName, BlockAtBootHotkeyOffRefusedMessage, place);
                return;
            }

            string action = blockAtBoot ? GateVerbs.SetBootOn : GateVerbs.SetBootOff;

            // A gate change: once it is running it is waited for, so the block before closing reads the setting it left.
            // Turning it off also allows nodes that are still blocked (see BlockCoordinator.SetBlockAtBootAsync).
            ControllerResult? result = await RunOperationAsync(action, ct => _coordinator.SetBlockAtBootAsync(blockAtBoot, place, ct), place, alwaysShowCard: viaHotkey);
            if (result is { IsSuccess: true })
            {
                AnnounceIfEnabled(blockAtBoot ? VoiceLine.BlockedAtBoot : VoiceLine.AllowedAtBoot);
            }
            else if (result is { IsSuccess: false } && result.UserMessage != BlockCoordinator.SessionEndingMessage)
            {
                // A change refused because the session began to end meanwhile was not tried, so it did not fail.
                AnnounceIfEnabled(VoiceLine.BlockFailed);
            }
        }
        finally
        {
            _operationsInFlight--;
            UpdatePresentation(forceIcon: false);
        }
    }

    // See BlockAtBootAsync for why the busy guard sits here, claimed before any await, rather than
    // relying on RunOperationAsync's own counter alone.
    private void OnProtectAudioClicked(bool viaHotkey = false)
    {
        if (_closing)
        {
            return;
        }

        CardPlace place = viaHotkey ? CardPlace.NearTray : ClickPlace();

        // Before the setting is saved: a refused change must not leave the saved setting flipped with nothing
        // applied. Nothing between this check and the coordinator's own awaits, so the two cannot disagree.
        if (RefusedAtSessionEnd("protect audio quality", TrayStatus.AppName, place))
        {
            return;
        }

        if (IsBusy || _coordinator.IsBusy)
        {
            ShowCard(TrayStatus.AppName, BusyMessage, place);
            return;
        }

        bool protect = !_registry.Settings.Current.ProtectAudioQuality;
        if (!TryUpdateSettings("protect audio quality", s => s.ProtectAudioQuality = protect, place))
        {
            return;
        }

        // The coordinator applies it in the right order and shows the microphone caveat once. Claimed
        // synchronously (_operationsInFlight++ inside RunOperationAsync runs before Launch returns, since
        // nothing between here and there awaits), so a burst of hotkey presses only starts one of these.
        string action = protect ? GateVerbs.ProtectOn : GateVerbs.ProtectOff;
        Launch(action, () => RunOperationAsync(action, ct => _coordinator.SetProtectionAsync(protect, place, ct), place, alwaysShowCard: viaHotkey), place);
    }

    // The menu tick toggles the saved HandBackOnShutdownAndSleep setting; nothing else runs from a click on it,
    // since the hand-back itself only ever runs from OnSessionEnding (shut down) or OnPowerChanged (sleep).
    // Refused while a session end is in progress, like every setting change.
    private void OnHandBackClicked()
    {
        if (_closing)
        {
            return;
        }

        CardPlace place = ClickPlace();
        if (RefusedAtSessionEnd("hand back at shut down and sleep", TrayStatus.AppName, place))
        {
            return;
        }

        bool on = !_registry.Settings.Current.HandBackOnShutdownAndSleep;
        TryUpdateSettings("hand back at shut down and sleep", s => s.HandBackOnShutdownAndSleep = on, place);
    }

    private void OnOpenOnStartupClicked()
    {
        if (_closing)
        {
            return;
        }

        CardPlace place = ClickPlace();
        _startupState = _startup.Read();
        bool openOnStartup = _startupState != StartupState.On;
        if (openOnStartup && _startupState == StartupState.DisabledInWindows)
        {
            _log.Warn("Open on startup: " + StartupRegistration.TurnedOffInWindowsMessage);
            ShowCard(TrayStatus.AppName, StartupRegistration.TurnedOffInWindowsMessage, place);
            return;
        }

        if (!TryUpdateSettings("open on startup", s => s.OpenOnStartup = openOnStartup, place))
        {
            return;
        }

        Report("open-on-startup", _startup.Apply(openOnStartup), place);
        _startupState = _startup.Read();
    }

    // First run: apply the default (on). In safe mode, and against a test data folder, StartupRegistration
    // writes nothing and logs it.
    private void ApplyStartupDefault()
    {
        bool openOnStartup = _registry.Settings.Current.OpenOnStartup;
        ControllerResult result = _startup.Apply(openOnStartup);
        if (_startup.WritesBlocked)
        {
            _log.Info((_registry.SafeMode ? "First run in safe mode: " : "First run against a test data folder: ") +
                "Open on startup default (" + (openOnStartup ? "on" : "off") + ") not applied.");
        }
        else
        {
            Report("open-on-startup (first run)", result, CardPlace.NearTray);
        }

        _startupState = _startup.Read();
    }

    // Any later start with Open on startup on: the Run value is written again when it is missing or
    // starts another file, for example after the Earshot folder moved. The tray owns this value.
    //
    // With Open on startup off, a Run value is removed only when the file it starts is no longer there: uninstall
    // removes %ProgramFiles%\Earshot but runs elevated, so it cannot reach this user's Run value (a separate profile
    // under Administrator protection), and the tray has no removal of its own. Earshot run again after uninstall then
    // leaves no entry behind that starts nothing; with the setting on, the repair below points it at this copy.
    //
    // It is left alone when the settings could not be read (the default would be written over the user's
    // choice), and when Windows started Earshot from that same value, which Microsoft's Run key guidance
    // asks a program not to write while it runs.
    // https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
    private void RepairStartupIfStale(SettingsLoadStatus status)
    {
        if (!_registry.Settings.Current.OpenOnStartup)
        {
            RemoveStartupIfTargetGone(status);
            return;
        }

        if (!SettingsWereRead(status))
        {
            _log.Info("Settings could not be read (" + status + "), so the Open on startup value is left as it is.");
            return;
        }

        if (_startedAtLogon)
        {
            _log.Info("Earshot was started by its Run value, so that value is not written again in this run.");
            return;
        }

        if (!_startup.RunValueNeedsRepair())
        {
            return;
        }

        if (_startup.WritesBlocked)
        {
            _log.Info(_startup.BlockedMessage + " The Open on startup Run value is missing or starts another file, and was not repaired.");
            return;
        }

        _log.Info("Open on startup is on, but the Run value is missing or starts another file. Writing it again.");
        Report("open-on-startup (repair)", _startup.Apply(true), CardPlace.NearTray);
        _startupState = _startup.Read();
    }

    // Open on startup is off: a Run value left behind that starts a file which is no longer there is removed.
    private void RemoveStartupIfTargetGone(SettingsLoadStatus status)
    {
        if (!SettingsWereRead(status) || _startedAtLogon || !_startup.RunValueTargetMissing())
        {
            return;
        }

        if (_startup.WritesBlocked)
        {
            _log.Info(_startup.BlockedMessage + " The Open on startup Run value starts " + _startup.RunValueTarget() +
                ", which is no longer there, and was not removed.");
            return;
        }

        _log.Info("Open on startup is off, and the Run value starts " + _startup.RunValueTarget() + ", which is no longer there. Removing it.");
        Report("open-on-startup (remove stale value)", _startup.Apply(false), CardPlace.NearTray);
        _startupState = _startup.Read();
    }

    // True when settings.json holds the user's own choice. A load that only put defaults in memory (the file
    // could not be read, or was unusable and reset) is not a choice to write back to HKCU.
    private static bool SettingsWereRead(SettingsLoadStatus status) =>
        status is SettingsLoadStatus.Loaded or SettingsLoadStatus.CreatedDefaults or
                  SettingsLoadStatus.RestoredFromBackup or SettingsLoadStatus.NewerSchema;

    private async Task ExitAsync(CardPlace place, string why = "Exit chosen from the tray menu.")
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _log.Info(why);
        try
        {
            // No more input: the icon goes, the picker closes, and everything in flight is cancelled. The
            // coordinator then blocks enabled nodes that are not in use, after any clean-up in flight.
            _notifyIcon.Visible = false;
            _picker?.Close();
            _exitPlace = place;
            _coordinator.BeginShutdown();
            _lifetime.Cancel();

            bool gaveUp = false;
            if (_pending.Count > 0 || _coordinator.IsBusy)
            {
                _log.Info("Waiting for " + DescribePending() + " to finish before closing.");

                // The block before closing on its own first reads the state and may block nothing, so it gets no
                // card until the block is sent (OnCoordinatorChanged).
                bool onlyTheBlock = _pending.Count == 0 && !_coordinator.IsBusyBeyondClosingBlock;
                if (!onlyTheBlock)
                {
                    place.Show(_registry.Cards, TrayStatus.AppName, ClosingMessage);
                }

                Task all = Task.WhenAll(_pending.Keys.Append(_coordinator.WhenIdleAsync()));
                TimeSpan waited = _exitWaitLimit;
                if (!await CompletesWithinAsync(all, _exitWaitLimit) && _coordinator.IsBusy && _coordinatorExitWaitLimit > _exitWaitLimit)
                {
                    // What the coordinator has in flight may still end in the block that keeps the nodes disabled at
                    // rest, and every wait inside it has a limit of its own.
                    _log.Info("Still waiting for the block coordinator after " + Seconds(_exitWaitLimit) + ", up to " +
                        Seconds(_coordinatorExitWaitLimit) + " in all, so a block its work ends with is still sent.");
                    await CompletesWithinAsync(_coordinator.WhenIdleAsync(), _coordinatorExitWaitLimit - _exitWaitLimit);
                    waited = _coordinatorExitWaitLimit;
                }

                if (all.IsFaulted)
                {
                    _log.Error("An action failed while Earshot was closing.", all.Exception);
                }
                else if (!all.IsCompleted)
                {
                    gaveUp = true;
                    _log.Warn("Closing after " + Seconds(waited) + " with " + DescribePending() + " still in flight.");
                }
            }

            // A change still running when the wait ran out may leave the nodes enabled, with nothing left here
            // to block them; the BootBlock task is then what blocks them at the next start.
            string? notice = _coordinator.ClosingNotice ??
                             (gaveUp && _coordinator.IsBusy && BlockStatus is not { BlockAtBoot: false, BlockAtBootKnown: true } ? BlockCoordinator.ClosedBeforeChangeEndedMessage : null);
            if (notice is not null)
            {
                _log.Info("Exit: " + notice);
                place.Show(_registry.Cards, TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), notice);
                await Task.Delay(_exitNoticeTime);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Exit: unexpected error while waiting for actions in flight.", ex);
        }
        finally
        {
            ExitThread();
        }
    }

    // True when task completed within limit.
    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan limit)
    {
        using var cancel = new CancellationTokenSource();
        Task first = await Task.WhenAny(task, Task.Delay(limit, cancel.Token));
        await cancel.CancelAsync();
        return first == task;
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

    private string DescribePending() =>
        _pending.Count.ToString(CultureInfo.InvariantCulture) + " action(s) (" + string.Join(", ", _pending.Values) + ")" +
        (_coordinator.IsBusy ? " and the block coordinator" : "");

    private async Task ChooseDeviceAsync(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        // No picker for a choice that would be refused once made.
        if (_picker is null && RefusedAtSessionEnd("choose device", TrayStatus.AppName, place))
        {
            return;
        }

        // The user asked for the picker again, so bringing the open one forward is expected.
        if (_picker is not null)
        {
            _picker.Activate();
            return;
        }

        PickerChoice? choice;
        EarshotSettings settings = _registry.Settings.Current;
        using (var form = new DevicePickerForm(settings.DeviceMatch, settings.PinnedContainerId))
        {
            _picker = form;
            try
            {
                Task load = LoadDevicesAsync(form);
                DialogResult dialogResult = form.ShowDialog();
                choice = dialogResult == DialogResult.OK ? form.Choice() : null;
                await load;
            }
            finally
            {
                _picker = null;
            }
        }

        if (choice is null || _closing)
        {
            return;
        }

        // A different device is a new intent: a connect or disconnect in flight for the old one is cancelled,
        // and the pin waits for its clean-up, so any re-block still acts on the device the gate pins now.
        if (_toggle is not null)
        {
            _toggleCancelReason = "another device was chosen";
            _toggle.Cancel();
        }

        try
        {
            await _coordinator.WhenIdleAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (!_closing)
        {
            await ApplyDeviceChoiceAsync(choice, place);
        }
    }

    // Pins a chosen device. Once the boot block is set up, the SYSTEM gate keeps its own copy of the device and
    // may refuse one (a device with no A2DP sink node, or services still turned off on the old device), so the
    // gate is asked first and the tray pins the device only once the gate has taken it: a refusal never leaves
    // the tray and the gate on different devices. Before setup there is no gate copy, and setup passes the
    // pinned device from settings. Internal so tests can drive it without the picker window.
    internal async Task ApplyDeviceChoiceAsync(PickerChoice choice, CardPlace place)
    {
        ArgumentNullException.ThrowIfNull(choice);

        // Asked here as well as in RunOperationAsync: before setup the device is only saved, with no operation.
        if (RefusedAtSessionEnd(GateVerbs.SetDevice, choice.Name, place))
        {
            return;
        }

        BootBlockStatus? status;
        try
        {
            status = await _coordinator.ReadBlockStatusAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (_closing)
        {
            return;
        }

        bool gateRepinned = false;
        if (status is not null && !TrayStatus.NeedsSetUp(status))
        {
            ControllerResult? result = await RunOperationAsync(GateVerbs.SetDevice, ct => _coordinator.ChangeDeviceAsync(choice.Address, ct), place);
            if (result is not { IsSuccess: true })
            {
                // The refusal or failure is already logged and on a card; the tray keeps the device it had.
                _log.Warn("Device not changed to " + choice.Name + " (" + choice.Address + "): the boot block did not take it" +
                    (result is null ? "." : " (" + result.Status + ")."));
                return;
            }

            gateRepinned = true;
        }

        if (_closing || !TryUpdateSettings(
                "device",
                s =>
                {
                    s.DeviceMatch = choice.Match;
                    s.PinnedContainerId = choice.ContainerId;
                    s.PinnedAddress = choice.Address;
                },
                place))
        {
            if (gateRepinned)
            {
                _log.Warn("The boot block now uses " + choice.Address + ", but the tray could not save it, so a connect that needs an allow is refused until the device is chosen again.");
            }

            return;
        }

        _pinAttemptedFor = choice.ContainerId;
        _log.Info("Device chosen: " + choice.Name + " (" + choice.Address + ", container " + choice.ContainerId + "), match \"" + choice.Match + "\".");
        _ = RefreshSnapshotAsync();

        if (status is null)
        {
            _log.Warn("The boot block status could not be read, so the gate was not re-pinned to " + choice.Address + ".");
            ShowCard(choice.Name, GateNotRepinnedMessage, place);
        }
        else if (!gateRepinned)
        {
            _log.Info("Boot block is not set up, so there is no gate copy of the device to update.");
        }
    }

    private async Task LoadDevicesAsync(DevicePickerForm form)
    {
        try
        {
            PairedDeviceList list = await Task.Run(_devices.Read, _lifetime.Token);
            if (!form.IsDisposed)
            {
                form.ShowDevices(list);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _log.Info("Device list read cancelled because the tray is closing.");
        }
        catch (Exception ex)
        {
            _log.Error("The paired Bluetooth devices could not be listed.", ex);
            if (!form.IsDisposed)
            {
                form.ShowReadFailed();
            }
        }
    }

    // Pins the resolved target's container, and the address of its BTHENUM\DEV_ node, the first time a
    // device matching the name is seen. Tried once per container per run, and never from a read that failed.
    private async Task PinIfFirstSightingAsync()
    {
        DeviceModel? target = TrayStatus.PinCandidate(_snapshot, _registry.Settings.Current);
        if (_closing || target is null || _pinAttemptedFor == target.ContainerId)
        {
            return;
        }

        _pinAttemptedFor = target.ContainerId;
        PairedDeviceList list;
        try
        {
            list = await Task.Run(_devices.Read, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Error("The device could not be pinned: the paired Bluetooth devices could not be listed.", ex);
            return;
        }

        // The snapshot or the settings may have changed while the list was read.
        if (_closing || TrayStatus.PinCandidate(_snapshot, _registry.Settings.Current)?.ContainerId != target.ContainerId)
        {
            return;
        }

        string? address = BluetoothDeviceList.AddressForContainer(list.Devices, target.ContainerId);
        if (address is null)
        {
            _log.Warn("No single BTHENUM\\DEV_ node found in container " + target.ContainerId + ", so only the container is pinned.");
        }

        if (TryUpdateSettings(
                "pin device",
                s =>
                {
                    s.PinnedContainerId = target.ContainerId;
                    s.PinnedAddress = address ?? "";
                },
                CardPlace.NearTray))
        {
            _log.Info("Pinned " + target.DisplayName + ": container " + target.ContainerId + ", address " + (address ?? "(none)") + ".");
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        try
        {
            DeviceSnapshot snapshot = await _registry.Monitor.RefreshAsync(_lifetime.Token);
            if (!_closed)
            {
                _snapshot = snapshot;
                UpdatePresentation(forceIcon: false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _log.Info("Device refresh cancelled because the tray is closing.");
        }
        catch (Exception ex)
        {
            _log.Error("The device state could not be refreshed.", ex);
        }
    }

    // Runs one menu action and returns its result, or null when it did not run, was cancelled or threw (each
    // logged, and shown unless Earshot is closing). Its token is cancelled only when Earshot closes: a click or
    // another action never cancels it. alwaysShowCard is set for a hotkey trigger, which has no menu tick to
    // show the result: a success then gets a card too, not only a failure.
    private async Task<ControllerResult?> RunOperationAsync(
        string action,
        Func<CancellationToken, Task<ControllerResult>> operation,
        CardPlace place,
        bool alwaysShowCard = false)
    {
        if (_closing)
        {
            return null;
        }

        // Every menu action that goes to the gate or to setup comes through here (setup, Block at boot, protection,
        // the device), so none of them starts while a session end is in progress, and none raises the busy guard.
        if (RefusedAtSessionEnd(action, TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), place))
        {
            return null;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationsInFlight++;
        UpdatePresentation(forceIcon: false);
        try
        {
            ControllerResult result = await operation(cts.Token);

            // While closing, Report logs the result and shows no card.
            Report(action, result, place, alwaysShowCard);
            return result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _log.Info(action + ": cancelled because Earshot is closing.");
            return null;
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, place);
            return null;
        }
        finally
        {
            _operationsInFlight--;
            UpdatePresentation(forceIcon: false);
            _ = _coordinator.RefreshStatusAsync();
        }
    }

    // Logs every result; shows a card for any result that is not a success, and for a success too when
    // alwaysShowCard asks for it (a hotkey trigger, which the owner cannot otherwise see the result of).
    private void Report(string action, ControllerResult result, CardPlace place, bool alwaysShowCard = false)
    {
        string text = TrayReport.Describe(action, result.Status, result.UserMessage, result.Steps);
        if (result.IsSuccess)
        {
            _log.Info(text);
            if (alwaysShowCard)
            {
                ShowCard(TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), result.UserMessage, place);
            }

            return;
        }

        _log.Warn(text);
        ShowCard(TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), result.UserMessage, place);
    }

    private bool TryUpdateSettings(string what, Action<EarshotSettings> mutate, CardPlace place)
    {
        try
        {
            _registry.Settings.Update(mutate);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Error("Settings were not saved (" + what + ").", ex);
            ShowCard(TrayStatus.AppName, SettingsNotSavedMessage, place);
            return false;
        }
    }

    private void ReportSettingsLoad(SettingsLoadStatus status)
    {
        string? message = status switch
        {
            SettingsLoadStatus.RestoredFromBackup => SettingsRestoredMessage,
            SettingsLoadStatus.ResetAfterCorruption => SettingsResetMessage,
            SettingsLoadStatus.ReadFailed => SettingsUnreadableMessage,
            SettingsLoadStatus.NewerSchema => SettingsNewerMessage,
            _ => null,
        };

        if (message is not null)
        {
            ShowCard(TrayStatus.AppName, message, CardPlace.NearTray);
        }
    }

    // Every card goes through registry.Cards. None is shown once Exit is chosen; the message behind it
    // has already been logged by the caller.
    private void ShowCard(string title, string status, CardPlace place)
    {
        if (_closing || _closed)
        {
            return;
        }

        place.Show(_registry.Cards, title, status);
    }
}

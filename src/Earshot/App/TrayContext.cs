using System.Drawing;
using System.Globalization;
using Earshot.Audio.Connect;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Icons;
using Earshot.Infra;
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

    private readonly ServiceRegistry _registry;
    private readonly BlockCoordinator _coordinator;
    private readonly ILog _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;
    private readonly ShellMessageWindow _window;
    private readonly HotkeyManager _hotkeys;
    private readonly Func<ISpeechEngine> _voiceEngineFactory;
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

        // Global shortcuts, over the same hidden window: no second window, no second message pump.
        // Every action a shortcut can raise goes through the exact method the matching menu item or the
        // left click already uses, so a hotkey is never a second route to the device.
        _hotkeys = new HotkeyManager(_window, options.NativeHotkeys, _log);
        _hotkeys.Activated += OnHotkeyActivated;
        _voiceEngineFactory = options.VoiceEngineFactory;

        _menu = new TrayMenu(CurrentMenuState);
        _menu.ToggleClicked += (_, _) => StartToggle();
        _menu.BlockAtBootClicked += (_, _) => Start("block at boot", place => BlockAtBootAsync(place));
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
            _notifyIcon.MouseClick -= OnIconMouseClick;
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

        // Before anything is claimed or shown: a refused connect raises no busy guard and says only why. A
        // disconnect still runs while the session ends (see BlockCoordinator.ToggleAsync).
        if (wanted && RefusedAtSessionEnd("connect", intent.DeviceName, place))
        {
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
        if (!_closed)
        {
            _coordinator.OnSessionEnding(e);
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
        MenuModel.Build(_snapshot, BlockStatus, _coordinator.ProtectionStatus, _registry.Settings.Current, IsBusy || _coordinator.IsBusy, _startupState, _registry.SafeMode, _voiceKnownNoVoice);

    private void UpdatePresentation(bool forceIcon)
    {
        if (_closed)
        {
            return;
        }

        RefreshIcon(remeasure: forceIcon, force: forceIcon);
        _notifyIcon.Text = TrayStatus.Tooltip(_snapshot, BlockStatus, _registry.Settings.Current);
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

    private async Task ExitAsync(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _log.Info("Exit chosen from the tray menu.");
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

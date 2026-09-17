using System.Drawing;
using System.Globalization;
using Earshot.Audio.Connect;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Infra;
using Earshot.Tray;

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
}

// The tray: icon, tooltip, menu, left click and the cards they raise. Runs on the UI thread only.
//
// Every device action goes through the BlockCoordinator, which owns connect, disconnect, block, allow and
// protection order and shows the cards for them. The tray decides what a click means, shows what it decides
// before handing over (nothing found, busy, settings), and logs every result with its steps.
//
// Left click: MouseClick with the left button only (Click and MouseClick also fire for the right and
// middle buttons, with X = Y = 0), ignored while a connect or disconnect is in flight so a fast triple
// click cannot toggle twice, ignored with a card while a menu action is in flight, and ignored while the
// device itself reports the link is changing, which is when the menu item is disabled too.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.mouseclick
//
// Cancellation: each connect or disconnect has its own token, cancelled when a different device is
// chosen or Earshot closes. Each menu action (setup, Block at boot, protection, device change) has its
// own token too, cancelled only when Earshot closes, so a click never cancels one. A cancellation stops the
// coordinator's waits, never a gate change it has already sent: that is waited for before the clean-up.
// Nothing here reads IBlockController.IsSetUp: that checks a scheduled task, which is not UI thread work,
// so setup is decided from the boot block status the coordinator reads asynchronously.
//
// Exit: no new input, cancel what is in flight, then wait for it and for the coordinator (at most
// ExitWaitLimit) before the message loop ends, so a cancelled connect can finish putting the device nodes
// back to blocked and the coordinator can block enabled nodes that are not in use. The Exit click shows a
// card that Earshot waits for the current change only when a change other than the check before closing is
// in flight; "Blocking" is shown only once the coordinator is about to send the block. What the user should
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
    public const string ClosingMessage = "Closing once the current change finishes.";
    public const string BlockingBeforeClosingMessage = "Blocking the AirPods before closing.";
    public const string BlockStatusUnreadableMessage = "Could not read the boot block status.";
    public const string GateNotRepinnedMessage = "Device saved. Boot block may still use the previous device.";

    // Long enough for a cancelled connect to finish its own clean-up, which may run the gate once more to
    // block the device nodes; short enough that a controller that never returns cannot keep Earshot
    // running. Whatever is still in flight when it runs out is logged by name.
    public static readonly TimeSpan DefaultExitWaitLimit = TimeSpan.FromSeconds(30);

    // How long a card shown as Earshot closes stays readable before the process ends: about one card's own
    // dismiss time. A UI timing choice, not a measurement.
    public static readonly TimeSpan DefaultExitNoticeTime = TimeSpan.FromSeconds(4);

    private readonly ServiceRegistry _registry;
    private readonly BlockCoordinator _coordinator;
    private readonly ILog _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;
    private readonly ShellMessageWindow _window;
    private readonly TrayIconFactory _icons;
    private readonly StartupRegistration _startup;
    private readonly BluetoothDeviceList _devices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _exitWaitLimit;
    private readonly TimeSpan _exitNoticeTime;
    private readonly Func<Point> _cursorPosition;
    private readonly bool _startedAtLogon;

    // Actions started from the icon or the menu that have not finished, by name.
    private readonly Dictionary<Task, string> _pending = new();

    private DeviceSnapshot _snapshot;
    private StartupState _startupState;
    private CancellationTokenSource? _toggle;
    private bool _toggleInFlight;
    private int _operationsInFlight;
    private Guid _pinAttemptedFor;
    private DevicePickerForm? _picker;
    private CardPlace? _exitPlace;
    private bool _blockingCardShown;
    private bool _closing;
    private bool _closed;

    public TrayContext(ServiceRegistry registry, BlockCoordinator coordinator, TrayStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _coordinator = coordinator;
        _log = registry.Log;
        _exitWaitLimit = options.ExitWaitLimit;
        _exitNoticeTime = options.ExitNoticeTime;
        _cursorPosition = options.CursorPosition;
        _startedAtLogon = options.StartedAtLogon;
        _snapshot = registry.Monitor.Current;
        _devices = new BluetoothDeviceList(_log);
        _startup = new StartupRegistration(options.StartupRegistry, _log, registry.SafeMode, options.ExePath, options.DataRootRedirected);
        _icons = new TrayIconFactory(_log, new ThemeReader(_log));

        _window = new ShellMessageWindow(_log);
        // The shell re-adds the old icon after TaskbarCreated, so that one always swaps in a new icon.
        _window.TaskbarCreated += (_, _) => RefreshIcon(remeasure: true, force: true);
        _window.SettingChanged += (_, _) => RefreshIcon(remeasure: true, force: false);
        _window.DisplayChanged += (_, _) => RefreshIcon(remeasure: true, force: false);
        _window.SessionEnding += OnSessionEnding;

        _menu = new TrayMenu(CurrentMenuState);
        _menu.ToggleClicked += (_, _) => StartToggle();
        _menu.BlockAtBootClicked += (_, _) => Start("block at boot", BlockAtBootAsync);
        _menu.ProtectAudioClicked += (_, _) => OnProtectAudioClicked();
        _menu.OpenOnStartupClicked += (_, _) => OnOpenOnStartupClicked();
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
    // user's own action, so it is placed like a card after a click.
    public void ShowStatusCard()
    {
        if (_closing || _closed)
        {
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
    }

    // The cursor as it is now, for a card that follows this click however long the action takes.
    private CardPlace ClickPlace() => CardPlace.AtClick(_cursorPosition());

    private void StartToggle()
    {
        CardPlace place = ClickPlace();
        Launch("toggle", () => ToggleAsync(place), place);
    }

    private void Start(string action, Func<CardPlace, Task> work)
    {
        CardPlace place = ClickPlace();
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
    private async Task ToggleAsync(CardPlace place)
    {
        if (_toggleInFlight)
        {
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

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _toggle = cts;
        _toggleInFlight = true;
        UpdatePresentation(forceIcon: false);
        string action = intent.Connect ? "connect" : "disconnect";
        try
        {
            ToggleReport report = await _coordinator.ToggleAsync(
                new ToggleRequest(intent.Connect, intent.Container, intent.DeviceName, place), cts.Token);

            // A controller that reports a cancellation rather than throwing one is still a cancelled click.
            bool cancelled = report.Cancelled || cts.IsCancellationRequested;
            string text = TrayReport.Describe(
                cancelled ? action + " (cancelled because " + CancelReason() + ")" : action,
                report.Status,
                report.UserMessage,
                report.Steps);
            _log.Write(report.IsSuccess || cancelled ? LogLevel.Info : LogLevel.Warn, text);
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
            UpdatePresentation(forceIcon: false);
            _ = _coordinator.RefreshStatusAsync();
        }
    }

    private string CancelReason() => _lifetime.IsCancellationRequested ? "Earshot is closing" : "another device was chosen";

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
    private void OnSettingsChanged(object? sender, EarshotSettings e) =>
        _registry.UiPost(() =>
        {
            if (!_closed)
            {
                UpdatePresentation(forceIcon: false);
            }
        });

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
        MenuModel.Build(_snapshot, BlockStatus, _coordinator.ProtectionStatus, _registry.Settings.Current, IsBusy, _startupState, _registry.SafeMode);

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

    private Task RunSetupAsync(CardPlace place) =>
        RunOperationAsync("setup", ct => _coordinator.RunAsync("setup", _registry.Block.RunSetupAsync, ct), place);

    private async Task BlockAtBootAsync(CardPlace place)
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

        // Before setup there is no gate to take the setting, so the click runs setup instead.
        if (TrayStatus.NeedsSetUp(status))
        {
            await RunSetupAsync(place);
            return;
        }

        bool blockAtBoot = !status.BlockAtBoot;
        string action = blockAtBoot ? GateVerbs.SetBootOn : GateVerbs.SetBootOff;

        // A gate change: once it is running it is waited for, so the block before closing reads the setting it left.
        // Turning it off also allows nodes that are still blocked (see BlockCoordinator.SetBlockAtBootAsync).
        await RunOperationAsync(action, ct => _coordinator.SetBlockAtBootAsync(blockAtBoot, place, ct), place);
    }

    private void OnProtectAudioClicked()
    {
        if (_closing)
        {
            return;
        }

        CardPlace place = ClickPlace();
        bool protect = !_registry.Settings.Current.ProtectAudioQuality;
        if (!TryUpdateSettings("protect audio quality", s => s.ProtectAudioQuality = protect, place))
        {
            return;
        }

        // The coordinator applies it in the right order and shows the microphone caveat once.
        string action = protect ? GateVerbs.ProtectOn : GateVerbs.ProtectOff;
        Launch(action, () => RunOperationAsync(action, ct => _coordinator.SetProtectionAsync(protect, place, ct), place), place);
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
    // It is left alone when the settings could not be read (the default would be written over the user's
    // choice), and when Windows started Earshot from that same value, which Microsoft's Run key guidance
    // asks a program not to write while it runs.
    // https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
    private void RepairStartupIfStale(SettingsLoadStatus status)
    {
        if (!_registry.Settings.Current.OpenOnStartup)
        {
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
                using var limit = new CancellationTokenSource();
                Task first = await Task.WhenAny(all, Task.Delay(_exitWaitLimit, limit.Token));
                await limit.CancelAsync();
                if (all.IsFaulted)
                {
                    _log.Error("An action failed while Earshot was closing.", all.Exception);
                }
                else if (first != all)
                {
                    gaveUp = true;
                    _log.Warn("Closing after " + _exitWaitLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture) +
                        " s with " + DescribePending() + " still in flight.");
                }
            }

            // A change still running when the wait ran out may leave the nodes enabled, with nothing left here
            // to block them; the BootBlock task is then what blocks them at the next start.
            string? notice = _coordinator.ClosingNotice ??
                             (gaveUp && _coordinator.IsBusy && BlockStatus?.BlockAtBoot != false ? BlockCoordinator.ClosedBeforeChangeEndedMessage : null);
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

    private string DescribePending() =>
        _pending.Count.ToString(CultureInfo.InvariantCulture) + " action(s) (" + string.Join(", ", _pending.Values) + ")" +
        (_coordinator.IsBusy ? " and the block coordinator" : "");

    private async Task ChooseDeviceAsync(CardPlace place)
    {
        if (_closing)
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
        _toggle?.Cancel();
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
    // another action never cancels it.
    private async Task<ControllerResult?> RunOperationAsync(
        string action,
        Func<CancellationToken, Task<ControllerResult>> operation,
        CardPlace place)
    {
        if (_closing)
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
            Report(action, result, place);
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

    // Logs every result; shows a card for any result that is not a success.
    private void Report(string action, ControllerResult result, CardPlace place)
    {
        string text = TrayReport.Describe(action, result.Status, result.UserMessage, result.Steps);
        if (result.IsSuccess)
        {
            _log.Info(text);
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

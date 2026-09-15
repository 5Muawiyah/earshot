using System.Globalization;
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

    // False only in tests, so they add no icon to the user's notification area.
    public bool ShowIcon { get; init; } = true;
}

// The tray: icon, tooltip, menu, left click and the cards they raise. Runs on the UI thread only.
//
// Every device action goes through the ServiceRegistry, so with the null services the tray reports
// "not found" and "not set up" and changes nothing. Every result that is not a success is logged with
// its steps and shown on a card through registry.Cards; this class never draws a card itself.
//
// Left click: MouseClick with the left button only (Click and MouseClick also fire for the right and
// middle buttons, with X = Y = 0), ignored while a connect or disconnect is in flight so a fast triple
// click cannot toggle twice, and ignored with a card while a menu action is in flight.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.mouseclick
//
// Cancellation: each connect or disconnect has its own token, cancelled when a different device is
// chosen or Earshot closes. Each menu action (setup, Block at boot, protection, device change) has its
// own token too, cancelled only when Earshot closes, so a click never cancels one. Nothing here reads
// IBlockController.IsSetUp: that checks a scheduled task, which is not UI thread work, so setup is
// decided from the boot block status read asynchronously.
//
// Exit: no new input, cancel what is in flight, then wait for it (at most ExitWaitLimit) before the
// message loop ends, so a cancelled connect can finish putting the device nodes back to blocked.
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
    public const string BlockStatusUnreadableMessage = "Could not read the boot block status.";
    public const string GateNotRepinnedMessage = "Device saved. Boot block may still use the previous device.";

    // Long enough for a cancelled connect to finish its own clean-up, which may run the gate once more to
    // block the device nodes; short enough that a controller that never returns cannot keep Earshot
    // running. Whatever is still in flight when it runs out is logged by name.
    public static readonly TimeSpan DefaultExitWaitLimit = TimeSpan.FromSeconds(30);

    private readonly ServiceRegistry _registry;
    private readonly ILog _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;
    private readonly ShellMessageWindow _window;
    private readonly TrayIconFactory _icons;
    private readonly StartupRegistration _startup;
    private readonly BluetoothDeviceList _devices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _exitWaitLimit;

    // Actions started from the icon or the menu that have not finished, by name.
    private readonly Dictionary<Task, string> _pending = new();

    private DeviceSnapshot _snapshot;
    private BootBlockStatus? _blockStatus;
    private AudioProtectionSnapshot? _protectionStatus;
    private StartupState _startupState;
    private CancellationTokenSource? _toggle;
    private bool _toggleInFlight;
    private int _operationsInFlight;
    private bool _statusRefreshInFlight;
    private bool _statusRefreshAgain;
    private Guid _pinAttemptedFor;
    private DevicePickerForm? _picker;
    private bool _closing;
    private bool _closed;

    public TrayContext(ServiceRegistry registry, TrayStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _log = registry.Log;
        _exitWaitLimit = options.ExitWaitLimit;
        _snapshot = registry.Monitor.Current;
        _devices = new BluetoothDeviceList(_log);
        _startup = new StartupRegistration(options.StartupRegistry, _log, registry.SafeMode, options.ExePath);
        _icons = new TrayIconFactory(_log, new ThemeReader(_log));

        _window = new ShellMessageWindow(_log);
        // The shell re-adds the old icon after TaskbarCreated, so that one always swaps in a new icon.
        _window.TaskbarCreated += (_, _) => RefreshIcon(remeasure: true, force: true);
        _window.SettingChanged += (_, _) => RefreshIcon(remeasure: true, force: false);
        _window.DisplayChanged += (_, _) => RefreshIcon(remeasure: true, force: false);
        _window.SessionEnding += (_, e) => SessionEnding?.Invoke(this, e);

        _menu = new TrayMenu(CurrentMenuState);
        _menu.ToggleClicked += (_, _) => Launch("toggle", () => ToggleAsync(CardAnchor.NearCursor), CardAnchor.NearCursor);
        _menu.BlockAtBootClicked += (_, _) => Launch("block at boot", BlockAtBootAsync, CardAnchor.NearCursor);
        _menu.ProtectAudioClicked += (_, _) => OnProtectAudioClicked();
        _menu.OpenOnStartupClicked += (_, _) => OnOpenOnStartupClicked();
        // Posted, so the menu has closed before the modal picker opens.
        _menu.ChooseDeviceClicked += (_, _) => _registry.UiPost(() => Launch("choose device", ChooseDeviceAsync, CardAnchor.NearCursor));
        _menu.SetUpClicked += (_, _) => Launch("setup", RunSetupAsync, CardAnchor.NearCursor);
        _menu.ExitClicked += (_, _) => _ = ExitAsync();

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu.Strip };
        _notifyIcon.MouseClick += OnIconMouseClick;
        _notifyIcon.MouseDown += (_, _) => _ = RefreshStatusAsync();
        UpdatePresentation(forceIcon: true);
        _notifyIcon.Visible = options.ShowIcon;

        _registry.Monitor.SnapshotChanged += OnSnapshotChanged;
        _registry.Settings.Changed += OnSettingsChanged;

        _startupState = _startup.Read();
        if (options.FirstRun)
        {
            ApplyStartupDefault();
        }
        else
        {
            RepairStartupIfStale();
        }

        ReportSettingsLoad(options.SettingsStatus);
        _ = RefreshStatusAsync();
        _ = PinIfFirstSightingAsync();
    }

    // WM_QUERYENDSESSION and WM_ENDSESSION, already logged. Nothing acts on them yet.
    public event EventHandler<SessionEndingEventArgs>? SessionEnding;

    // True while a connect, disconnect or menu action is in flight.
    internal bool IsBusy => _toggleInFlight || _operationsInFlight > 0;

    // Actions from the icon or the menu that have not finished yet.
    internal int PendingActions => _pending.Count;

    internal TrayMenu Menu => _menu;

    // Shows the current state on a card, for example when a second copy of Earshot is started.
    public void ShowStatusCard()
    {
        if (_closing || _closed)
        {
            return;
        }

        EarshotSettings settings = _registry.Settings.Current;
        ShowCard(TrayStatus.DeviceName(_snapshot, settings), TrayStatus.CardStatus(_snapshot, _blockStatus, settings), CardAnchor.NearTray);
    }

    // Shows an unexpected exception from any handler on the tray thread. Program.Tray routes
    // Application.ThreadException here.
    internal void ReportUnexpected(string action, Exception ex, CardAnchor anchor)
    {
        _log.Error(action + ": unexpected error.", ex);
        ShowCard(TrayStatus.AppName, SomethingWentWrongMessage, anchor);
    }

    // The NotifyIcon.MouseClick handler. Internal so tests can raise each button.
    internal void OnIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Launch("toggle", () => ToggleAsync(CardAnchor.NearCursor), CardAnchor.NearCursor);
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

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closing = true;
        _closed = true;
        _lifetime.Cancel();
        _registry.Cards.Hide();
        _notifyIcon.Visible = false;
    }

    // Starts an action from the icon or the menu. An action still running is kept so Exit can wait for
    // it, and an exception the action did not handle is logged and shown rather than lost with a
    // discarded task.
    private void Launch(string action, Func<Task> work, CardAnchor anchor)
    {
        if (_closing)
        {
            _log.Info(action + ": ignored because Earshot is closing.");
            return;
        }

        Task task = RunReportedAsync(action, work, anchor);
        if (!task.IsCompleted)
        {
            _pending.Add(task, action);
            _ = ForgetWhenDoneAsync(task);
        }
    }

    private async Task RunReportedAsync(string action, Func<Task> work, CardAnchor anchor)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, anchor);
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

    // Toggles the connection of the pinned (or resolved) device.
    private async Task ToggleAsync(CardAnchor anchor)
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
            ShowCard(TrayStatus.DeviceName(_snapshot, settings), BusyMessage, anchor);
            return;
        }

        ToggleIntent? intent = TrayStatus.Intent(_snapshot, settings);
        if (intent is null)
        {
            string message = TrayStatus.NotFoundMessage(settings);
            _log.Warn("Toggle: " + message);
            ShowCard(settings.DeviceMatch, message, anchor);
            return;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _toggle = cts;
        _toggleInFlight = true;
        UpdatePresentation(forceIcon: false);
        string action = intent.Connect ? "connect" : "disconnect";
        try
        {
            if (intent.Connect)
            {
                ShowCard(intent.DeviceName, TrayStatus.CardConnecting, anchor);
            }

            ConnectResult result = await RunToggleAsync(intent, cts.Token);
            if (cts.IsCancellationRequested)
            {
                _log.Info(TrayReport.Describe(
                    action + " (cancelled because " + CancelReason() + ") " + result.Outcome,
                    result.Confirmed ? OpStatus.Success : OpStatus.Failed,
                    result.UserMessage,
                    result.Steps));
                return;
            }

            if (result.Confirmed)
            {
                _log.Info(TrayReport.Describe(action, OpStatus.Success, result.UserMessage, result.Steps));
                ShowCard(intent.DeviceName, intent.Connect ? TrayStatus.CardConnected : TrayStatus.CardDisconnected, anchor);
            }
            else
            {
                _log.Warn(TrayReport.Describe(action + " " + result.Outcome, OpStatus.Failed, result.UserMessage, result.Steps));
                ShowCard(intent.DeviceName, result.UserMessage, anchor);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _log.Info(action + ": cancelled because " + CancelReason() + ".");
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, anchor);
        }
        finally
        {
            _toggle = null;
            _toggleInFlight = false;
            UpdatePresentation(forceIcon: false);
            _ = RefreshStatusAsync();
        }
    }

    // The single call that changes the connection. Kept on its own so it can be routed through the
    // block coordinator without touching the click handling around it.
    private Task<ConnectResult> RunToggleAsync(ToggleIntent intent, CancellationToken ct) =>
        intent.Connect
            ? _registry.Connection.ConnectAsync(intent.Container, ct)
            : _registry.Connection.DisconnectAsync(intent.Container, ct);

    private string CancelReason() => _lifetime.IsCancellationRequested ? "Earshot is closing" : "another device was chosen";

    private void OnSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        if (_closing || _closed)
        {
            return;
        }

        _snapshot = e.Snapshot;
        UpdatePresentation(forceIcon: false);
        _ = PinIfFirstSightingAsync();
        _ = RefreshStatusAsync();
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

    private MenuState CurrentMenuState() =>
        MenuModel.Build(_snapshot, _blockStatus, _protectionStatus, _registry.Settings.Current, IsBusy, _startupState);

    private void UpdatePresentation(bool forceIcon)
    {
        if (_closed)
        {
            return;
        }

        RefreshIcon(remeasure: forceIcon, force: forceIcon);
        _notifyIcon.Text = TrayStatus.Tooltip(_snapshot, _blockStatus, _registry.Settings.Current);
    }

    private void RefreshIcon(bool remeasure, bool force)
    {
        if (_closed)
        {
            return;
        }

        GlyphState glyph = TrayStatus.Glyph(_snapshot, _blockStatus, _registry.Settings.Current, _toggleInFlight);
        _icons.Apply(_notifyIcon, glyph, remeasure, force);
    }

    private Task RunSetupAsync() =>
        RunOperationAsync("setup", ct => _registry.Block.RunSetupAsync(ct), CardAnchor.NearCursor);

    private async Task BlockAtBootAsync()
    {
        // The cached status is read again whenever the icon is pressed, which happens before the menu
        // opens; it is read here only when nothing has been read yet.
        BootBlockStatus? status = _blockStatus ?? await ReadBlockStatusAsync();
        if (_closing)
        {
            return;
        }

        if (status is null)
        {
            ShowCard(TrayStatus.AppName, BlockStatusUnreadableMessage, CardAnchor.NearCursor);
            return;
        }

        // Before setup there is no gate to take the setting, so the click runs setup instead.
        if (TrayStatus.NeedsSetUp(status))
        {
            await RunSetupAsync();
            return;
        }

        bool blockAtBoot = !status.BlockAtBoot;
        await RunOperationAsync(
            blockAtBoot ? "setboot-on" : "setboot-off",
            ct => _registry.Block.SetBlockAtBootAsync(blockAtBoot, ct),
            CardAnchor.NearCursor);
    }

    private void OnProtectAudioClicked()
    {
        if (_closing)
        {
            return;
        }

        bool protect = !_registry.Settings.Current.ProtectAudioQuality;
        if (!TryUpdateSettings("protect audio quality", s => s.ProtectAudioQuality = protect, CardAnchor.NearCursor))
        {
            return;
        }

        string action = protect ? "protect-on" : "protect-off";
        Launch(
            action,
            () => RunOperationAsync(
                action,
                ct => _registry.Protection.ApplyAsync(protect, ct),
                CardAnchor.NearCursor,
                onSuccess: result =>
                {
                    if (protect && result.Status == OpStatus.Success)
                    {
                        ShowMicrophoneNoticeOnce();
                    }
                }),
            CardAnchor.NearCursor);
    }

    // The microphone caveat, once, after the first successful protection change.
    private void ShowMicrophoneNoticeOnce()
    {
        EarshotSettings settings = _registry.Settings.Current;
        if (settings.ProtectAudioNoticeShown)
        {
            return;
        }

        ShowCard(TrayStatus.DeviceName(_snapshot, settings), TrayStatus.MicrophoneNotice, CardAnchor.NearTray);
        TryUpdateSettings("microphone notice", s => s.ProtectAudioNoticeShown = true, CardAnchor.NearTray);
    }

    private void OnOpenOnStartupClicked()
    {
        if (_closing)
        {
            return;
        }

        _startupState = _startup.Read();
        bool openOnStartup = _startupState != StartupState.On;
        if (openOnStartup && _startupState == StartupState.DisabledInWindows)
        {
            _log.Warn("Open on startup: " + StartupRegistration.TurnedOffInWindowsMessage);
            ShowCard(TrayStatus.AppName, StartupRegistration.TurnedOffInWindowsMessage, CardAnchor.NearCursor);
            return;
        }

        if (!TryUpdateSettings("open on startup", s => s.OpenOnStartup = openOnStartup, CardAnchor.NearCursor))
        {
            return;
        }

        Report("open-on-startup", _startup.Apply(openOnStartup), CardAnchor.NearCursor);
        _startupState = _startup.Read();
    }

    // First run: apply the default (on). In safe mode StartupRegistration writes nothing and logs it.
    private void ApplyStartupDefault()
    {
        bool openOnStartup = _registry.Settings.Current.OpenOnStartup;
        ControllerResult result = _startup.Apply(openOnStartup);
        if (_registry.SafeMode)
        {
            _log.Info("First run in safe mode: Open on startup default (" + (openOnStartup ? "on" : "off") + ") not applied.");
        }
        else
        {
            Report("open-on-startup (first run)", result, CardAnchor.NearTray);
        }

        _startupState = _startup.Read();
    }

    // Any later start with Open on startup on: the Run value is written again when it is missing or
    // starts another file, for example after the Earshot folder moved. The tray owns this value.
    private void RepairStartupIfStale()
    {
        if (!_registry.Settings.Current.OpenOnStartup || !_startup.RunValueNeedsRepair())
        {
            return;
        }

        if (_registry.SafeMode)
        {
            _log.Info("Safe mode: the Open on startup Run value is missing or starts another file, and was not repaired.");
            return;
        }

        _log.Info("Open on startup is on, but the Run value is missing or starts another file. Writing it again.");
        Report("open-on-startup (repair)", _startup.Apply(true), CardAnchor.NearTray);
        _startupState = _startup.Read();
    }

    private async Task ExitAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _log.Info("Exit chosen from the tray menu.");
        try
        {
            // No more input: the icon goes, the picker closes, and everything in flight is cancelled.
            _notifyIcon.Visible = false;
            _picker?.Close();
            _lifetime.Cancel();

            if (_pending.Count > 0)
            {
                _log.Info("Waiting for " + DescribePending() + " to finish before closing.");
                _registry.Cards.Show(new CardContent(TrayStatus.AppName, ClosingMessage), CardAnchor.NearTray);

                Task all = Task.WhenAll(_pending.Keys);
                using var limit = new CancellationTokenSource();
                Task first = await Task.WhenAny(all, Task.Delay(_exitWaitLimit, limit.Token));
                await limit.CancelAsync();
                if (all.IsFaulted)
                {
                    _log.Error("An action failed while Earshot was closing.", all.Exception);
                }
                else if (first != all)
                {
                    _log.Warn("Closing after " + _exitWaitLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture) +
                        " s with " + DescribePending() + " still in flight.");
                }
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
        _pending.Count.ToString(CultureInfo.InvariantCulture) + " action(s) (" + string.Join(", ", _pending.Values) + ")";

    private async Task ChooseDeviceAsync()
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

        // A different device is a new intent: a connect or disconnect in flight for the old one is cancelled.
        _toggle?.Cancel();
        if (!TryUpdateSettings(
                "device",
                s =>
                {
                    s.DeviceMatch = choice.Match;
                    s.PinnedContainerId = choice.ContainerId;
                    s.PinnedAddress = choice.Address;
                },
                CardAnchor.NearCursor))
        {
            return;
        }

        _pinAttemptedFor = choice.ContainerId;
        _log.Info("Device chosen: " + choice.Name + " (" + choice.Address + ", container " + choice.ContainerId + "), match \"" + choice.Match + "\".");
        _ = RefreshSnapshotAsync();

        // The SYSTEM gate keeps its own copy of the device identity, so it is re-pinned too when it is set
        // up. Before setup there is no copy: setup passes the pinned device from settings.
        BootBlockStatus? status = await ReadBlockStatusAsync();
        if (_closing)
        {
            return;
        }

        if (status is null)
        {
            _log.Warn("The boot block status could not be read, so the gate was not re-pinned to " + choice.Address + ".");
            ShowCard(choice.Name, GateNotRepinnedMessage, CardAnchor.NearCursor);
            return;
        }

        if (TrayStatus.NeedsSetUp(status))
        {
            _log.Info("Boot block is not set up, so there is no gate copy of the device to update.");
            return;
        }

        await RunOperationAsync("set-device", ct => _registry.Block.SetDeviceAsync(choice.Address, ct), CardAnchor.NearCursor);
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
    // device matching the name is seen. Tried once per container per run.
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
                CardAnchor.NearTray))
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

    // Reads the boot block status now and caches it. Null when the read failed (logged with its code) or
    // Earshot is closing.
    private async Task<BootBlockStatus?> ReadBlockStatusAsync()
    {
        CancellationToken ct = _lifetime.Token;
        try
        {
            BootBlockStatus status = await _registry.Block.GetStatusAsync(ct);
            _blockStatus = status;
            return status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogReadFailure("block-status", "The boot block status could not be read.", ex);
            return null;
        }
    }

    // Reads the boot block and protection status in the background. Overlapping requests are merged
    // into one more pass.
    private async Task RefreshStatusAsync()
    {
        if (_closed)
        {
            return;
        }

        if (_statusRefreshInFlight)
        {
            _statusRefreshAgain = true;
            return;
        }

        _statusRefreshInFlight = true;
        try
        {
            do
            {
                _statusRefreshAgain = false;
                CancellationToken ct = _lifetime.Token;
                try
                {
                    _blockStatus = await _registry.Block.GetStatusAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogReadFailure("block-status", "The boot block status could not be read.", ex);
                }

                try
                {
                    _protectionStatus = await _registry.Protection.GetStatusAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogReadFailure("protection-status", "The audio protection status could not be read.", ex);
                }

                UpdatePresentation(forceIcon: false);
            }
            while (_statusRefreshAgain && !_closed);
        }
        finally
        {
            _statusRefreshInFlight = false;
        }
    }

    private void LogReadFailure(string step, string message, Exception ex)
    {
        StepOutcome outcome = StepOutcomes.FromHResult(step, ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
        _log.Error(message + " " + TrayReport.DescribeStep(outcome), ex);
    }

    // Runs one menu action. Its token is cancelled only when Earshot closes: a click or another action
    // never cancels it.
    private async Task RunOperationAsync(
        string action,
        Func<CancellationToken, Task<ControllerResult>> operation,
        CardAnchor anchor,
        Action<ControllerResult>? onSuccess = null)
    {
        if (_closing)
        {
            return;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationsInFlight++;
        UpdatePresentation(forceIcon: false);
        try
        {
            ControllerResult result = await operation(cts.Token);

            // While closing, Report logs the result and shows no card, and the one-time notice waits for
            // a run in which it can be seen.
            Report(action, result, anchor);
            if (result.IsSuccess && !_closing)
            {
                onSuccess?.Invoke(result);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _log.Info(action + ": cancelled because Earshot is closing.");
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, anchor);
        }
        finally
        {
            _operationsInFlight--;
            UpdatePresentation(forceIcon: false);
            _ = RefreshStatusAsync();
        }
    }

    // Logs every result; shows a card for any result that is not a success.
    private void Report(string action, ControllerResult result, CardAnchor anchor)
    {
        string text = TrayReport.Describe(action, result.Status, result.UserMessage, result.Steps);
        if (result.IsSuccess)
        {
            _log.Info(text);
            return;
        }

        _log.Warn(text);
        ShowCard(TrayStatus.DeviceName(_snapshot, _registry.Settings.Current), result.UserMessage, anchor);
    }

    private bool TryUpdateSettings(string what, Action<EarshotSettings> mutate, CardAnchor anchor)
    {
        try
        {
            _registry.Settings.Update(mutate);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Error("Settings were not saved (" + what + ").", ex);
            ShowCard(TrayStatus.AppName, SettingsNotSavedMessage, anchor);
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
            ShowCard(TrayStatus.AppName, message, CardAnchor.NearTray);
        }
    }

    // Every card goes through registry.Cards. None is shown once Exit is chosen; the message behind it
    // has already been logged by the caller.
    private void ShowCard(string title, string status, CardAnchor anchor)
    {
        if (_closing || _closed)
        {
            return;
        }

        _registry.Cards.Show(new CardContent(title, status), anchor);
    }
}

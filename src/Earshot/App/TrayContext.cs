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
    IStartupRegistry StartupRegistry);

// The tray: icon, tooltip, menu, left click and the cards they raise. Runs on the UI thread only.
//
// Every device action goes through the ServiceRegistry, so with the null services the tray reports
// "not found" and "not set up" and changes nothing. Every result that is not a success is logged with
// its steps and shown on a card through registry.Cards; this class never draws a card itself.
//
// Left click: MouseClick with the left button only (Click and MouseClick also fire for the right and
// middle buttons, with X = Y = 0), ignored while a toggle is in flight so a fast triple click cannot
// toggle twice. Each new intent (a toggle, a menu action, a device change, Exit) cancels the token of
// the previous one.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon.mouseclick
internal sealed class TrayContext : ApplicationContext
{
    public const string SomethingWentWrongMessage = "Something went wrong. See the log.";
    public const string SettingsNotSavedMessage = "Settings could not be saved.";
    public const string SettingsRestoredMessage = "Settings were restored from a backup.";
    public const string SettingsResetMessage = "Settings were reset.";
    public const string SettingsUnreadableMessage = "Settings could not be read.";
    public const string SettingsNewerMessage = "Settings are from a newer Earshot. Changes will not be saved.";

    private readonly ServiceRegistry _registry;
    private readonly ILog _log;
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayMenu _menu;
    private readonly ShellMessageWindow _window;
    private readonly TrayIconFactory _icons;
    private readonly StartupRegistration _startup;
    private readonly BluetoothDeviceList _devices;
    private readonly CancellationTokenSource _lifetime = new();

    private DeviceSnapshot _snapshot;
    private BootBlockStatus? _blockStatus;
    private AudioProtectionSnapshot? _protectionStatus;
    private StartupState _startupState;
    private CancellationTokenSource? _intent;
    private bool _toggleInFlight;
    private int _operationsInFlight;
    private bool _statusRefreshInFlight;
    private bool _statusRefreshAgain;
    private Guid _pinAttemptedFor;
    private DevicePickerForm? _picker;
    private bool _closed;

    public TrayContext(ServiceRegistry registry, TrayStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _log = registry.Log;
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
        _menu.ToggleClicked += (_, _) => _ = ToggleAsync(CardAnchor.NearCursor);
        _menu.BlockAtBootClicked += (_, _) => OnBlockAtBootClicked();
        _menu.ProtectAudioClicked += (_, _) => OnProtectAudioClicked();
        _menu.OpenOnStartupClicked += (_, _) => OnOpenOnStartupClicked();
        _menu.ChooseDeviceClicked += (_, _) => _registry.UiPost(() => _ = ChooseDeviceAsync());
        _menu.SetUpClicked += (_, _) => _ = RunOperationAsync("setup", ct => _registry.Block.RunSetupAsync(ct), CardAnchor.NearCursor);
        _menu.ExitClicked += (_, _) => OnExitClicked();

        _notifyIcon = new NotifyIcon { ContextMenuStrip = _menu.Strip };
        _notifyIcon.MouseClick += OnIconMouseClick;
        _notifyIcon.MouseDown += (_, _) => _ = RefreshStatusAsync();
        UpdatePresentation(forceIcon: true);
        _notifyIcon.Visible = true;

        _registry.Monitor.SnapshotChanged += OnSnapshotChanged;
        _registry.Settings.Changed += OnSettingsChanged;

        _startupState = _startup.Read();
        if (options.FirstRun)
        {
            ApplyStartupDefault();
        }

        ReportSettingsLoad(options.SettingsStatus);
        _ = RefreshStatusAsync();
        _ = PinIfFirstSightingAsync();
    }

    // WM_QUERYENDSESSION and WM_ENDSESSION, already logged. Nothing acts on them yet.
    public event EventHandler<SessionEndingEventArgs>? SessionEnding;

    private bool IsBusy => _toggleInFlight || _operationsInFlight > 0;

    // Shows the current state on a card, for example when a second copy of Earshot is started.
    public void ShowStatusCard()
    {
        if (_closed)
        {
            return;
        }

        EarshotSettings settings = _registry.Settings.Current;
        ShowCard(TrayStatus.DeviceName(_snapshot, settings), TrayStatus.CardStatus(_snapshot, _blockStatus, settings), CardAnchor.NearTray);
    }

    // Toggles the connection of the pinned (or resolved) device.
    internal async Task ToggleAsync(CardAnchor anchor)
    {
        if (_closed)
        {
            return;
        }

        if (_toggleInFlight)
        {
            _log.Write(LogLevel.Debug, "Click ignored: a connect or disconnect is already in flight.");
            return;
        }

        EarshotSettings settings = _registry.Settings.Current;
        ToggleIntent? intent = TrayStatus.Intent(_snapshot, settings);
        if (intent is null)
        {
            string message = TrayStatus.NotFoundMessage(settings);
            _log.Warn("Toggle: " + message);
            ShowCard(settings.DeviceMatch, message, anchor);
            return;
        }

        CancellationTokenSource cts = BeginIntent();
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
                _log.Info(action + ": finished after a newer request replaced it: " + result.Outcome + ".");
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
            _log.Info(action + ": cancelled by a newer request.");
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, anchor);
        }
        finally
        {
            _toggleInFlight = false;
            EndIntent(cts);
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

    // Shows an unexpected exception from any handler on the tray thread. Program.Tray routes
    // Application.ThreadException here.
    internal void ReportUnexpected(string action, Exception ex, CardAnchor anchor)
    {
        _log.Error(action + ": unexpected error.", ex);
        ShowCard(TrayStatus.AppName, SomethingWentWrongMessage, anchor);
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
            _intent?.Dispose();
            _intent = null;
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

        _closed = true;
        _lifetime.Cancel();
        _intent?.Cancel();
        _registry.Cards.Hide();
        _notifyIcon.Visible = false;
    }

    private void OnIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _ = ToggleAsync(CardAnchor.NearCursor);
    }

    private void OnSnapshotChanged(object? sender, DeviceSnapshotEventArgs e)
    {
        if (_closed)
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
        MenuModel.Build(_snapshot, _blockStatus, _protectionStatus, _registry.Settings.Current, IsBusy, _registry.SafeMode, _startupState);

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

        _icons.Apply(_notifyIcon, TrayStatus.Glyph(_snapshot, _blockStatus, _toggleInFlight), remeasure, force);
    }

    private void OnBlockAtBootClicked()
    {
        // Before setup there is no gate to take the setting, so the click runs setup instead.
        if (!_registry.Block.IsSetUp)
        {
            _ = RunOperationAsync("setup", ct => _registry.Block.RunSetupAsync(ct), CardAnchor.NearCursor);
            return;
        }

        bool blockAtBoot = !(_blockStatus?.BlockAtBoot ?? new GateConfig().BlockAtBoot);
        _ = RunOperationAsync(
            blockAtBoot ? "setboot-on" : "setboot-off",
            ct => _registry.Block.SetBlockAtBootAsync(blockAtBoot, ct),
            CardAnchor.NearCursor);
    }

    private void OnProtectAudioClicked()
    {
        bool protect = !_registry.Settings.Current.ProtectAudioQuality;
        if (!TryUpdateSettings("protect audio quality", s => s.ProtectAudioQuality = protect, CardAnchor.NearCursor))
        {
            return;
        }

        _ = RunOperationAsync(
            protect ? "protect-on" : "protect-off",
            ct => _registry.Protection.ApplyAsync(protect, ct),
            CardAnchor.NearCursor,
            onSuccess: result =>
            {
                if (protect && result.Status == OpStatus.Success)
                {
                    ShowMicrophoneNoticeOnce();
                }
            });
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

    private void OnExitClicked()
    {
        _log.Info("Exit chosen from the tray menu.");
        ExitThread();
    }

    private async Task ChooseDeviceAsync()
    {
        if (_closed)
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

        if (choice is null || _closed)
        {
            return;
        }

        // A different device is a new intent: anything in flight for the old one is cancelled.
        CancelIntent();
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

        // The SYSTEM gate keeps its own copy of the device identity, so it is re-pinned too.
        if (_registry.Block.IsSetUp)
        {
            await RunOperationAsync("set-device", ct => _registry.Block.SetDeviceAsync(choice.Address, ct), CardAnchor.NearCursor);
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
    // device matching the name is seen. Tried once per container per run.
    private async Task PinIfFirstSightingAsync()
    {
        DeviceModel? target = TrayStatus.PinCandidate(_snapshot, _registry.Settings.Current);
        if (_closed || target is null || _pinAttemptedFor == target.ContainerId)
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
        if (_closed || TrayStatus.PinCandidate(_snapshot, _registry.Settings.Current)?.ContainerId != target.ContainerId)
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
                    _log.Error("The boot block status could not be read.", ex);
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
                    _log.Error("The audio protection status could not be read.", ex);
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

    private async Task RunOperationAsync(
        string action,
        Func<CancellationToken, Task<ControllerResult>> operation,
        CardAnchor anchor,
        Action<ControllerResult>? onSuccess = null)
    {
        if (_closed)
        {
            return;
        }

        CancellationTokenSource cts = BeginIntent();
        _operationsInFlight++;
        UpdatePresentation(forceIcon: false);
        try
        {
            ControllerResult result = await operation(cts.Token);
            Report(action, result, anchor);
            if (result.IsSuccess && !cts.IsCancellationRequested)
            {
                onSuccess?.Invoke(result);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _log.Info(action + ": cancelled by a newer request.");
        }
        catch (Exception ex)
        {
            ReportUnexpected(action, ex, anchor);
        }
        finally
        {
            _operationsInFlight--;
            EndIntent(cts);
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

    private void ShowCard(string title, string status, CardAnchor anchor)
    {
        if (_closed)
        {
            return;
        }

        _registry.Cards.Show(new CardContent(title, status), anchor);
    }

    // A new intent cancels the previous one's token.
    private CancellationTokenSource BeginIntent()
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = _intent;
        _intent = cts;
        previous?.Cancel();
        return cts;
    }

    private void CancelIntent() => _intent?.Cancel();

    private void EndIntent(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_intent, cts))
        {
            _intent = null;
        }

        cts.Dispose();
    }
}

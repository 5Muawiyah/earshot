using System.ComponentModel;
using Earshot.Streaming;

namespace Earshot.Tray;

// The low battery threshold submenu entry a click landed on, the same shape as StreamingMenuItemEventArgs.
internal sealed class LowBatteryThresholdMenuItemEventArgs(LowBatteryThresholdMenuItem item) : EventArgs
{
    public LowBatteryThresholdMenuItem Item { get; } = item;
}

// The tray's right-click menu. It renders a MenuState and raises one event per command.
//
// The type is not called ContextMenu because WinForms still ships a System.Windows.Forms.ContextMenu
// placeholder, and the two names would clash wherever both namespaces are imported.
//
// Text, Checked and Enabled are refreshed only in Opening, from the state function the owner passes in.
// That function must read cached state only: no I/O and no device calls on this path.
// https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.toolstripdropdown.opening
internal sealed class TrayMenu : IDisposable
{
    private readonly Func<MenuState> _state;
    private readonly ToolStripMenuItem _safeMode = new();
    private readonly ToolStripMenuItem _toggle = new();
    private readonly ToolStripMenuItem _playFromPhone = new();
    private readonly ToolStripMenuItem _blockAtBoot = new();
    private readonly ToolStripMenuItem _handBack = new();
    private readonly ToolStripMenuItem _protectAudio = new();
    private readonly ToolStripMenuItem _protectCaveat = new();
    private readonly ToolStripMenuItem _openOnStartup = new();
    private readonly ToolStripMenuItem _speakStatus = new();
    private readonly ToolStripMenuItem _showOnTaskbar = new();
    private readonly ToolStripMenuItem _leftClickConnects = new();
    private readonly ToolStripMenuItem _caseOpenCard = new();
    private readonly ToolStripMenuItem _lowBatteryAlert = new();
    private readonly ToolStripMenuItem _lowBatteryThreshold = new();
    private readonly ToolStripMenuItem _claimAirPods = new();
    private readonly ToolStripMenuItem _nameOtherDevice = new();
    private readonly ToolStripMenuItem _chooseDevice = new();
    private readonly ToolStripMenuItem _setUp = new();
    private readonly ToolStripMenuItem _checkForUpdates = new();
    private readonly ToolStripMenuItem _checkAutomatically = new();
    private readonly ToolStripMenuItem _exit = new();

    public TrayMenu(Func<MenuState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;

        Strip = new ContextMenuStrip();
        Strip.Items.AddRange(
        [
            _safeMode,
            _toggle,
            _playFromPhone,
            new ToolStripSeparator(),
            _blockAtBoot,
            _handBack,
            _protectAudio,
            _protectCaveat,
            _openOnStartup,
            _speakStatus,
            new ToolStripSeparator(),
            _showOnTaskbar,
            _leftClickConnects,
            _caseOpenCard,
            _lowBatteryAlert,
            _lowBatteryThreshold,
            _claimAirPods,
            _nameOtherDevice,
            new ToolStripSeparator(),
            _chooseDevice,
            _setUp,
            new ToolStripSeparator(),
            _checkForUpdates,
            _checkAutomatically,
            new ToolStripSeparator(),
            _exit,
        ]);

        _toggle.Click += (_, _) => ToggleClicked?.Invoke(this, EventArgs.Empty);
        _blockAtBoot.Click += (_, _) => BlockAtBootClicked?.Invoke(this, EventArgs.Empty);
        _handBack.Click += (_, _) => HandBackClicked?.Invoke(this, EventArgs.Empty);
        _protectAudio.Click += (_, _) => ProtectAudioClicked?.Invoke(this, EventArgs.Empty);
        _openOnStartup.Click += (_, _) => OpenOnStartupClicked?.Invoke(this, EventArgs.Empty);
        _speakStatus.Click += (_, _) => SpeakStatusClicked?.Invoke(this, EventArgs.Empty);
        _showOnTaskbar.Click += (_, _) => ShowOnTaskbarClicked?.Invoke(this, EventArgs.Empty);
        _leftClickConnects.Click += (_, _) => LeftClickConnectsClicked?.Invoke(this, EventArgs.Empty);
        _caseOpenCard.Click += (_, _) => CaseOpenCardClicked?.Invoke(this, EventArgs.Empty);
        _lowBatteryAlert.Click += (_, _) => LowBatteryAlertClicked?.Invoke(this, EventArgs.Empty);
        _claimAirPods.Click += (_, _) => ClaimAirPodsClicked?.Invoke(this, EventArgs.Empty);
        _nameOtherDevice.Click += (_, _) => NameOtherDeviceClicked?.Invoke(this, EventArgs.Empty);
        _chooseDevice.Click += (_, _) => ChooseDeviceClicked?.Invoke(this, EventArgs.Empty);
        _setUp.Click += (_, _) => SetUpClicked?.Invoke(this, EventArgs.Empty);
        _checkForUpdates.Click += (_, _) => CheckForUpdatesClicked?.Invoke(this, EventArgs.Empty);
        _checkAutomatically.Click += (_, _) => CheckAutomaticallyClicked?.Invoke(this, EventArgs.Empty);
        _exit.Click += (_, _) => ExitClicked?.Invoke(this, EventArgs.Empty);
        Strip.Opening += OnOpening;

        Apply(state());
    }

    public event EventHandler? ToggleClicked;

    // One item of the Play from a phone submenu was clicked: a device, Stop, or Refresh the list.
    public event EventHandler<StreamingMenuItemEventArgs>? PlayFromPhoneItemClicked;

    public event EventHandler? BlockAtBootClicked;

    public event EventHandler? HandBackClicked;

    public event EventHandler? ProtectAudioClicked;

    public event EventHandler? OpenOnStartupClicked;

    public event EventHandler? SpeakStatusClicked;

    public event EventHandler? ShowOnTaskbarClicked;

    public event EventHandler? LeftClickConnectsClicked;

    public event EventHandler? CaseOpenCardClicked;

    public event EventHandler? LowBatteryAlertClicked;

    // One entry of the low battery threshold submenu was clicked.
    public event EventHandler<LowBatteryThresholdMenuItemEventArgs>? LowBatteryThresholdItemClicked;

    public event EventHandler? ClaimAirPodsClicked;

    public event EventHandler? NameOtherDeviceClicked;

    public event EventHandler? ChooseDeviceClicked;

    public event EventHandler? SetUpClicked;

    public event EventHandler? CheckForUpdatesClicked;

    public event EventHandler? CheckAutomaticallyClicked;

    public event EventHandler? ExitClicked;

    public ContextMenuStrip Strip { get; }

    // The items in display order, for tests.
    internal IReadOnlyList<ToolStripItem> Items => Strip.Items.Cast<ToolStripItem>().ToArray();

    // The Play from a phone submenu in display order, for tests.
    internal IReadOnlyList<ToolStripMenuItem> PlayFromPhoneItems => _playFromPhone.DropDownItems.OfType<ToolStripMenuItem>().ToArray();

    // The low battery threshold submenu in display order, for tests.
    internal IReadOnlyList<ToolStripMenuItem> LowBatteryThresholdItems => _lowBatteryThreshold.DropDownItems.OfType<ToolStripMenuItem>().ToArray();

    internal void Apply(MenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Set(_safeMode, state.SafeMode);
        Set(_toggle, state.Toggle);
        Set(_playFromPhone, state.PlayFromPhone);
        SetPlayFromPhoneItems(state.PlayFromPhoneItems);
        Set(_blockAtBoot, state.BlockAtBoot);
        Set(_handBack, state.HandBack);
        Set(_protectAudio, state.ProtectAudio);
        Set(_protectCaveat, state.ProtectCaveat);
        Set(_openOnStartup, state.OpenOnStartup);
        Set(_speakStatus, state.SpeakStatus);
        Set(_showOnTaskbar, state.ShowOnTaskbar);
        Set(_leftClickConnects, state.LeftClickConnectsItem);
        Set(_caseOpenCard, state.CaseOpenCardItem);
        Set(_lowBatteryAlert, state.LowBatteryAlert);
        Set(_lowBatteryThreshold, state.LowBatteryThreshold);
        SetLowBatteryThresholdItems(state.LowBatteryThresholdItems);
        Set(_claimAirPods, state.ClaimAirPodsItem);
        Set(_nameOtherDevice, state.NameOtherDeviceItem);
        Set(_chooseDevice, state.ChooseDevice);
        Set(_setUp, state.SetUp);
        Set(_checkForUpdates, state.CheckForUpdates);
        Set(_checkAutomatically, state.CheckAutomatically);
        Set(_exit, state.Exit);
    }

    public void Dispose() => Strip.Dispose();

    // What Opening does, for tests that cannot open the menu on screen.
    internal void Refresh() => Apply(_state());

    private void OnOpening(object? sender, CancelEventArgs e) => Refresh();

    // The submenu is whatever the state says, rebuilt each time: a sentence is a disabled item, and a device, Stop
    // and Refresh the list raise PlayFromPhoneItemClicked with the item they were built from. The device id rides on
    // that item and never reaches any text. A name is whatever the phone's owner typed, so "&" is doubled: on its own
    // a menu reads it as "underline the next letter".
    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.toolstripitem.text
    private void SetPlayFromPhoneItems(IReadOnlyList<StreamingMenuItem> items)
    {
        ToolStripItem[] old = _playFromPhone.DropDownItems.Cast<ToolStripItem>().ToArray();
        _playFromPhone.DropDownItems.Clear();
        foreach (ToolStripItem item in old)
        {
            item.Click -= OnPlayFromPhoneItemClick;
            item.Dispose();
        }

        foreach (StreamingMenuItem item in items)
        {
            var child = new ToolStripMenuItem
            {
                Text = item.Text.Replace("&", "&&", StringComparison.Ordinal),
                Checked = item.Checked,
                Enabled = item.Enabled && item.Command != StreamingMenuCommand.None,
                Tag = item,
            };
            child.Click += OnPlayFromPhoneItemClick;
            _playFromPhone.DropDownItems.Add(child);
        }
    }

    private void OnPlayFromPhoneItemClick(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem { Tag: StreamingMenuItem item })
        {
            PlayFromPhoneItemClicked?.Invoke(this, new StreamingMenuItemEventArgs(item));
        }
    }

    // The submenu is rebuilt each time, exactly as SetPlayFromPhoneItems is: nine fixed percentages, so
    // there is never a stale entry left over from an earlier threshold. Lives under its own
    // _lowBatteryThreshold parent, never under _lowBatteryAlert: that item is a plain checkable toggle, not
    // a dropdown, so a click on it always means "turn the alert on or off", never "open a submenu".
    private void SetLowBatteryThresholdItems(IReadOnlyList<LowBatteryThresholdMenuItem> items)
    {
        ToolStripItem[] old = _lowBatteryThreshold.DropDownItems.Cast<ToolStripItem>().ToArray();
        _lowBatteryThreshold.DropDownItems.Clear();
        foreach (ToolStripItem item in old)
        {
            item.Click -= OnLowBatteryThresholdItemClick;
            item.Dispose();
        }

        foreach (LowBatteryThresholdMenuItem item in items)
        {
            var child = new ToolStripMenuItem
            {
                Text = item.Text,
                Checked = item.Checked,
                Enabled = item.Enabled,
                Tag = item,
            };
            child.Click += OnLowBatteryThresholdItemClick;
            _lowBatteryThreshold.DropDownItems.Add(child);
        }
    }

    private void OnLowBatteryThresholdItemClick(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem { Tag: LowBatteryThresholdMenuItem item })
        {
            LowBatteryThresholdItemClicked?.Invoke(this, new LowBatteryThresholdMenuItemEventArgs(item));
        }
    }

    private static void Set(ToolStripMenuItem item, MenuItemState state)
    {
        item.Text = state.Text;
        item.CheckState = state.Indeterminate ? CheckState.Indeterminate
                        : state.Checked ? CheckState.Checked
                        : CheckState.Unchecked;
        item.Enabled = state.Enabled;

        // Available, not Visible: Visible also reports whether the closed menu is on screen.
        item.Available = state.Visible;
    }
}

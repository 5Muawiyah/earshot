using Earshot.Contracts;

namespace Earshot.Tray;

// The device the user picked, and the name text Earshot matches it by.
internal sealed record PickerChoice(string Match, string Name, string Address, Guid ContainerId);

// Lists paired Bluetooth devices and a name-match box. Renamed AirPods no longer match "AirPods", so
// this is how the user points Earshot at the right device.
//
// Matching is an ordinal, case-insensitive "contains", never a wildcard, so a curly apostrophe in the
// device name is kept as it is. OK is enabled only when a usable device is selected and its name
// contains the match text.
//
// The form has no public properties (WFO1000); the owner reads the choice through Choice().
internal sealed class DevicePickerForm : Form
{
    public const string Title = "Choose device";
    public const string NameColumn = "Name";
    public const string AddressColumn = "Address";
    public const string MatchLabel = "Name contains";
    public const string MismatchHint = "Must be part of the device name.";
    public const string NoDevicesMessage = "No paired Bluetooth devices.";
    public const string ReadFailedMessage = "Could not read Bluetooth devices.";
    public const string OkText = "OK";
    public const string CancelText = "Cancel";

    private readonly Guid _pinnedContainer;
    private readonly ListView _list;
    private readonly TextBox _match;
    private readonly Label _hint;
    private readonly Button _ok;
    private IReadOnlyList<PairedDevice> _devices = [];

    public DevicePickerForm(string currentMatch, Guid pinnedContainer)
    {
        _pinnedContainer = pinnedContainer;

        Text = Title;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(440, 320);
        MinimumSize = new Size(360, 260);
        Padding = new Padding(12);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add(NameColumn, 260);
        _list.Columns.Add(AddressColumn, 140);
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.DoubleClick += (_, _) => AcceptIfReady();

        var matchLabel = new Label
        {
            Text = MatchLabel,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0),
        };

        _match = new TextBox
        {
            Text = currentMatch ?? "",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        _match.TextChanged += (_, _) => UpdateAcceptState();

        _hint = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0),
        };

        _ok = new Button { Text = OkText, DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
        var cancel = new Button { Text = CancelText, DialogResult = DialogResult.Cancel, AutoSize = true };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
            WrapContents = false,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_list, 0, 0);
        layout.SetColumnSpan(_list, 2);
        layout.Controls.Add(matchLabel, 0, 1);
        layout.Controls.Add(_match, 1, 1);
        layout.Controls.Add(_hint, 1, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = _ok;
        CancelButton = cancel;
    }

    // Formats a 12-hex address as pairs, for example 5A:6B:7C:8D:9E:AF.
    internal static string FormatAddress(string address12)
    {
        if (!BoundaryValidation.IsAddress12(address12))
        {
            return address12;
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => address12.Substring(i * 2, 2)));
    }

    // True when the device can be pinned and its name contains the match text.
    internal static bool CanAccept(PairedDevice? device, string? match) =>
        device is not null &&
        NodeMatch.IsValidTargetContainer(device.ContainerId) &&
        BoundaryValidation.IsAddress12(device.Address) &&
        !string.IsNullOrWhiteSpace(match) &&
        NodeMatch.NameMatches(device.Name, match.Trim());

    // Fills the list. Called on the UI thread once the read finishes.
    internal void ShowDevices(PairedDeviceList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _devices = list.Devices;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (PairedDevice device in list.Devices)
            {
                var item = new ListViewItem(device.Name) { Tag = device };
                item.SubItems.Add(FormatAddress(device.Address));

                // A device that is paired but not in range is greyed.
                if (device.IsPresent == false)
                {
                    item.ForeColor = SystemColors.GrayText;
                }

                _list.Items.Add(item);
                if (device.ContainerId == _pinnedContainer && _pinnedContainer != Guid.Empty)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }

            if (list.Devices.Count > 0)
            {
                // Name fits its longest entry; the last column takes the remaining width.
                _list.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
                _list.AutoResizeColumn(1, ColumnHeaderAutoResizeStyle.HeaderSize);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        if (list.Devices.Count == 0)
        {
            _hint.Text = list.Problems.Count > 0 ? ReadFailedMessage : NoDevicesMessage;
        }

        UpdateAcceptState();
    }

    // Shown when the read itself failed.
    internal void ShowReadFailed()
    {
        _hint.Text = ReadFailedMessage;
        UpdateAcceptState();
    }

    // The accepted choice, or null when nothing usable is selected.
    internal PickerChoice? Choice()
    {
        PairedDevice? device = SelectedDevice();
        string match = _match.Text.Trim();
        return CanAccept(device, match)
            ? new PickerChoice(match, device!.Name, device.Address, device.ContainerId)
            : null;
    }

    private PairedDevice? SelectedDevice() =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as PairedDevice : null;

    private void OnSelectionChanged()
    {
        // Picking a device keeps the match text when the device name contains it, and otherwise puts the
        // device name in the box.
        if (SelectedDevice() is { } device && !NodeMatch.NameMatches(device.Name, _match.Text.Trim()))
        {
            _match.Text = device.Name;
        }

        UpdateAcceptState();
    }

    private void UpdateAcceptState()
    {
        PairedDevice? device = SelectedDevice();
        bool ready = CanAccept(device, _match.Text);
        _ok.Enabled = ready;

        if (device is not null && !string.IsNullOrWhiteSpace(_match.Text) && !ready)
        {
            _hint.Text = MismatchHint;
        }
        else if (_devices.Count > 0 || device is not null)
        {
            _hint.Text = "";
        }
    }

    private void AcceptIfReady()
    {
        if (_ok.Enabled)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

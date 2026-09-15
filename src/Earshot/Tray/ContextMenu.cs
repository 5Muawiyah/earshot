using System.ComponentModel;

namespace Earshot.Tray;

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
    private readonly ToolStripMenuItem _toggle = new();
    private readonly ToolStripMenuItem _blockAtBoot = new();
    private readonly ToolStripMenuItem _protectAudio = new();
    private readonly ToolStripMenuItem _protectCaveat = new();
    private readonly ToolStripMenuItem _openOnStartup = new();
    private readonly ToolStripMenuItem _chooseDevice = new();
    private readonly ToolStripMenuItem _setUp = new();
    private readonly ToolStripMenuItem _exit = new();

    public TrayMenu(Func<MenuState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;

        Strip = new ContextMenuStrip();
        Strip.Items.AddRange(
        [
            _toggle,
            new ToolStripSeparator(),
            _blockAtBoot,
            _protectAudio,
            _protectCaveat,
            _openOnStartup,
            new ToolStripSeparator(),
            _chooseDevice,
            _setUp,
            new ToolStripSeparator(),
            _exit,
        ]);

        _toggle.Click += (_, _) => ToggleClicked?.Invoke(this, EventArgs.Empty);
        _blockAtBoot.Click += (_, _) => BlockAtBootClicked?.Invoke(this, EventArgs.Empty);
        _protectAudio.Click += (_, _) => ProtectAudioClicked?.Invoke(this, EventArgs.Empty);
        _openOnStartup.Click += (_, _) => OpenOnStartupClicked?.Invoke(this, EventArgs.Empty);
        _chooseDevice.Click += (_, _) => ChooseDeviceClicked?.Invoke(this, EventArgs.Empty);
        _setUp.Click += (_, _) => SetUpClicked?.Invoke(this, EventArgs.Empty);
        _exit.Click += (_, _) => ExitClicked?.Invoke(this, EventArgs.Empty);
        Strip.Opening += OnOpening;

        Apply(state());
    }

    public event EventHandler? ToggleClicked;

    public event EventHandler? BlockAtBootClicked;

    public event EventHandler? ProtectAudioClicked;

    public event EventHandler? OpenOnStartupClicked;

    public event EventHandler? ChooseDeviceClicked;

    public event EventHandler? SetUpClicked;

    public event EventHandler? ExitClicked;

    public ContextMenuStrip Strip { get; }

    // The items in display order, for tests.
    internal IReadOnlyList<ToolStripItem> Items => Strip.Items.Cast<ToolStripItem>().ToArray();

    internal void Apply(MenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Set(_toggle, state.Toggle);
        Set(_blockAtBoot, state.BlockAtBoot);
        Set(_protectAudio, state.ProtectAudio);
        Set(_protectCaveat, state.ProtectCaveat);
        Set(_openOnStartup, state.OpenOnStartup);
        Set(_chooseDevice, state.ChooseDevice);
        Set(_setUp, state.SetUp);
        Set(_exit, state.Exit);
    }

    public void Dispose() => Strip.Dispose();

    // What Opening does, for tests that cannot open the menu on screen.
    internal void Refresh() => Apply(_state());

    private void OnOpening(object? sender, CancelEventArgs e) => Refresh();

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

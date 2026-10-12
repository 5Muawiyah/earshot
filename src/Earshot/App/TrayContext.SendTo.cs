using Earshot.Contracts;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Widget;

namespace Earshot.App;

// The tray's side of the send-to picker: where the AirPods are now and one choice to send them elsewhere. Three ways open it
// (the card's Send to row, the Send to item in the menu a right-click on the gauge opens, and the shortcut), each with its
// own switch (SendToAccess). Opening it touches nothing. A choice goes through the paths the Connect and Disconnect buttons and
// shortcuts already use, or opens Windows' sound settings, which never hands the AirPods back (SendToActions).
internal sealed partial class TrayContext
{
    // Windows' sound settings page.
    // https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
    internal const string SoundSettingsUri = "ms-settings:sound";

    private SendToPicker? _sendToPicker;

    // True while the menu on screen is the one a right-click on the gauge opened, so Send to is in it. The tray icon's own
    // menu never has it.
    private bool _menuOpenedFromGauge;

    // The menu a right-click on a gauge opens.
    private void ShowGaugeMenu(Point point)
    {
        _menuOpenedFromGauge = true;
        void Reset(object? sender, ToolStripDropDownClosedEventArgs e)
        {
            _menu.Strip.Closed -= Reset;
            _menuOpenedFromGauge = false;
        }

        _menu.Strip.Closed += Reset;
        _menu.Strip.Show(point);
    }

    // The shortcut. A press while the picker is open closes it, as the card shortcut closes the card.
    private void OnSendToHotkey()
    {
        _log.Info("Hotkey: open or close Send to.");
        if (_sendToPicker is { IsDisposed: false, Visible: true })
        {
            CloseSendTo();
            return;
        }

        OpenSendTo(openedByKeyboard: true, viaHotkey: true);
    }

    // Opens the picker above the gauge, or near the cursor when there is none. The UI thread.
    internal void OpenSendTo(bool openedByKeyboard, bool viaHotkey = false)
    {
        if (_closing)
        {
            return;
        }

        CloseSendTo();
        EarshotSettings settings = _registry.Settings.Current;
        ConnectionState connection = TrayStatus.ActiveTarget(_snapshot, settings)?.Connection ?? ConnectionState.Unknown;
        AirPodsWhere where = SendToAccess.Where(_widgetSnapshotCache.Where, connection == ConnectionState.Connected);
        SendToModel model = SendToModel.From(where, settings.Widget.OtherDeviceLabel, IsBusy);

        Point cursor = _cursorPosition();
        Rectangle anchor = SendToAnchor(cursor);
        var picker = new SendToPicker(_log, AccentColourService.Shared(_log), () => LookService().Current);
        picker.SetTheme(_widgetTheme?.Ink() ?? SystemColors.WindowText, SystemInformation.HighContrast);
        int dpi = _widgetLayoutDpi;
        Size size = picker.Prepare(model, dpi, openedByKeyboard);
        Rectangle workArea = SystemDisplaySource.WorkAreaFor(_displaySource, anchor);
        Rectangle bounds = WidgetCardPlacement.Above(anchor, size, workArea, dpi, settings.Widget.GaugePosition);
        picker.Chosen += (_, choice) => OnSendToChosen(picker, model, choice, viaHotkey);
        picker.Dismissed += (_, _) => CloseSendTo(picker);
        _sendToPicker = picker;
        picker.PresentAt(bounds);
    }

    // The gauge the picker goes above: the one the cursor is on (a right-click on a gauge of another display), else the main
    // one, else the cursor itself.
    private Rectangle SendToAnchor(Point cursor)
    {
        foreach (Rectangle gauge in ShownGaugeBounds())
        {
            if (gauge.Contains(cursor))
            {
                return gauge;
            }
        }

        return GaugeBoundsIfShown() ?? new Rectangle(cursor, Size.Empty);
    }

    private void OnSendToChosen(SendToPicker picker, SendToModel model, SendToChoice choice, bool viaHotkey)
    {
        CloseSendTo(picker);
        CardPlace place = viaHotkey ? CardPlace.NearTray : ClickPlace();
        _log.Info("Send to: " + choice + ".");
        var actions = new SendToActions(
            connect: () => StartToggle(viaHotkey, SwitchTrigger.SendTo, wanted: true),
            handBack: () => StartToggle(viaHotkey, SwitchTrigger.SendTo, wanted: false),
            openSoundSettings: () => Launch("open sound settings (send to)", () => OpenSettingsPageAsync(SoundSettingsUri, WidgetCopy.SoundSettingsNotOpened, place), place));
        actions.Choose(model, choice);
    }

    private async Task OpenSettingsPageAsync(string uri, string notOpened, CardPlace place)
    {
        bool opened = await _settingsLauncher.OpenAsync(uri, CancellationToken.None);
        if (!opened)
        {
            ShowCard(TrayStatus.AppName, notOpened, place);
        }
    }

    private void CloseSendTo(SendToPicker? only = null)
    {
        if (_sendToPicker is not { } picker || (only is not null && !ReferenceEquals(picker, only)))
        {
            return;
        }

        _sendToPicker = null;
        if (!picker.IsDisposed)
        {
            picker.Hide();
            picker.Dispose();
        }
    }

    // For tests.
    internal SendToPicker? SendToPickerForTest => _sendToPicker;
}

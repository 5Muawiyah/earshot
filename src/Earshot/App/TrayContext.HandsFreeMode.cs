using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tray;
using Earshot.Widget;

namespace Earshot.App;

// The tray's side of the Hands-Free "microphone off" mode (HandsFreeMicrophoneMode): the settings row's switch and its
// button. The mode is Protect audio quality turned off with a note, so the switch goes through the menu item's own path:
// the saved intent first, then the protection coordinator, which applies it in the right order and, with the nodes
// blocked, only keeps it until they are next allowed. Opening sound settings opens a page and changes nothing.
internal sealed partial class TrayContext
{
    private ISettingsLauncher _settingsLauncher = null!;

    // Called once, from the constructor. Safe mode and a run whose data lives somewhere other than the real folder (every
    // test run) never open a window of Windows Settings; a test that wants the call supplies a launcher of its own.
    private void WireSoundSettings(TrayStartOptions options)
    {
        _settingsLauncher = options.SettingsLauncher
            ?? (_registry.SafeMode || Paths.Current.IsRedirected
                ? new SafeSettingsLauncher(_log)
                : new WindowsSettingsLauncher(_log));
    }

    internal void SetHandsFreeMicrophoneOff(bool on, CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        // Before the setting is saved: a refused change must not leave the saved setting flipped with nothing applied.
        if (RefusedAtSessionEnd("microphone off mode", TrayStatus.AppName, place))
        {
            return;
        }

        if (IsBusy || _coordinator.IsBusy)
        {
            ShowCard(TrayStatus.AppName, BusyMessage, place);
            return;
        }

        if (HandsFreeMicrophoneMode.IsOn(_registry.Settings.Current) == on)
        {
            return;
        }

        if (!TryUpdateSettings("microphone off mode", s => HandsFreeMicrophoneMode.Switch(s, on), place))
        {
            return;
        }

        bool protect = !on;
        string action = protect ? GateVerbs.ProtectOn : GateVerbs.ProtectOff;
        Launch(action, () => RunOperationAsync(action, ct => _coordinator.SetProtectionAsync(protect, place, ct), place), place);
    }

    internal void OpenSoundSettings(CardPlace place)
    {
        if (_closing)
        {
            return;
        }

        MicrophoneRow row = HandsFreeMicrophoneMode.Describe(_snapshot);
        Launch("open sound settings", () => OpenSoundSettingsAsync(row.SettingsUri, place), place);
    }

    private async Task OpenSoundSettingsAsync(string uri, CardPlace place)
    {
        bool opened = await _settingsLauncher.OpenAsync(uri, CancellationToken.None);
        if (!opened)
        {
            ShowCard(TrayStatus.AppName, WidgetCopy.SoundSettingsNotOpened, place);
        }
    }
}

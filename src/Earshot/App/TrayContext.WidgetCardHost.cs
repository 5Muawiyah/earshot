using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Update;
using Earshot.Widget;

namespace Earshot.App;

// What the card's settings page and update page reach the tray through. Every setter is the write the tray menu's
// own item already makes (TryUpdateSettings, with the widget's watcher flag recomputed where the menu does), so a
// change here and the same change from the menu cannot disagree, and a failed save is reported on a card exactly as
// it is for them. Nothing here starts a connect, a disconnect or a node change.
internal sealed partial class TrayContext
{
    private WidgetCardHost? _widgetCardHost;

    // Raised when the update flow moves, on whatever thread moved it. The card's presenter posts to the UI thread.
    internal event EventHandler? CardUpdateChanged;

    private void RaiseCardUpdateChanged() => CardUpdateChanged?.Invoke(this, EventArgs.Empty);

    private WidgetCardHost CardHost => _widgetCardHost ??= new WidgetCardHost(this);

    // The host the card's pages use, for tests.
    internal IWidgetCardHost WidgetCardHostForTest => CardHost;

    // The hotkey settings as a scratch copy, so a chord can be checked without touching what is saved.
    private static HotkeySettings CopyOf(HotkeySettings source)
    {
        var copy = new HotkeySettings { Enabled = source.Enabled };
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            copy.SetText(action, source.TextFor(action));
        }

        return copy;
    }

    private sealed class WidgetCardHost : IWidgetCardHost
    {
        private readonly TrayContext _tray;

        public WidgetCardHost(TrayContext tray)
        {
            _tray = tray;
            tray.CardUpdateChanged += (_, _) => UpdateChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? UpdateChanged;

        public CardSettingsValues ReadSettings()
        {
            EarshotSettings settings = _tray._registry.Settings.Current;
            WidgetSettings widget = settings.Widget;
            HotkeyBindingModel hotkeys = _tray.HotkeyBindings(settings.Hotkeys);
            WidgetSnapshot snapshot = _tray._widgetSnapshotCache;
            ReleaseVersion? running = ReleaseVersion.Running(typeof(TrayContext).Assembly);

            // Pausing when a bud comes out is switched by its own setting but only acts once the in-ear signal is
            // known, so the page says it is still waiting while the snapshot says auto-pause is not available.
            DisplayReading displays = _tray._displaySource.Read();
            IReadOnlyList<DisplayOption> displayOptions = GaugeDisplayOptions.Build(displays.Displays);
            bool chosenConnected = string.IsNullOrEmpty(widget.GaugeDisplay) ||
                GaugeDisplayChoice.Resolve(widget.GaugeDisplay, displays.Displays).Fallback == DisplayFallbackReason.None;
            string? displayNote = !chosenConnected
                ? WidgetCopy.DisplayChosenNotConnected
                : _tray._gaugeController?.DisplayFallback == DisplayFallbackReason.TaskbarNotShown ? WidgetCopy.DisplayChosenNoTaskbar : null;
            return new CardSettingsValues(
                widget.GaugePosition,
                widget.OtherDeviceLabel,
                widget.AutoPause,
                settings.PauseWhenAirPodsLeave,
                widget.LowBatteryThresholdPercent,
                widget.LeftClickConnects,
                settings.HandBackOnShutdownAndSleep,
                hotkeys.Chord(HotkeyAction.SwitchToPc),
                hotkeys.Chord(HotkeyAction.SwitchToPhone),
                hotkeys.FailureMessage(HotkeyAction.SwitchToPc),
                hotkeys.FailureMessage(HotkeyAction.SwitchToPhone),
                running?.ToString(),
                settings.CheckForUpdatesAutomatically,
                InEarProofMissing: !snapshot.AutoPauseAvailable,
                InstallExists: _tray.BlockStatus is { InstallExists: true })
            {
                OpenCardChord = hotkeys.Chord(HotkeyAction.OpenCard),
                OpenCardFailure = hotkeys.FailureMessage(HotkeyAction.OpenCard),
                HandsFreeMicrophoneOff = HandsFreeMicrophoneMode.IsOn(settings),
                MicrophoneState = HandsFreeMicrophoneMode.Describe(_tray._snapshot).State,
                GaugeDisplayId = widget.GaugeDisplay,
                GaugeDisplayOptions = displayOptions,
                GaugeDisplayNote = displayNote,
                GaugeOrder = widget.GaugeOrder,
                GaugePreview = GaugeContent.From(snapshot, _tray._time.GetUtcNow(), new GaugeDisplaySettings(widget.LowBatteryThresholdPercent, widget.OtherDeviceLabel)),
                ElevatedRunNote = _tray._elevatedRun is { } run ? FinishingMessage(run) : null,
            };
        }

        public void SetGaugePosition(GaugePosition value, CardPlace place) =>
            Write("gauge position (card)", s => s.Widget = s.Widget with { GaugePosition = value }, place);

        public void SetGaugeOrder(GaugeOrder value, CardPlace place) =>
            Write("gauge order (card)", s => s.Widget = s.Widget with { GaugeOrder = GaugeOrders.FromStored(value) }, place);

        // A display that has gone since the list was drawn is not stored: the choice would be one nothing can honour, and the
        // page is redrawn with the displays that are there.
        public void SetGaugeDisplay(string id, CardPlace place)
        {
            if (!string.IsNullOrEmpty(id) && GaugeDisplayChoice.Resolve(id, _tray._displaySource.Read().Displays).Fallback != DisplayFallbackReason.None)
            {
                _tray._log.Info("Gauge display: the display chosen on the settings page is no longer connected, so the choice was not stored.");
                _tray._widgetCardPresenter?.Refresh();
                return;
            }

            Write("gauge display (card)", s => s.Widget = s.Widget with { GaugeDisplay = id }, place);
        }

        public void SetOtherDeviceLabel(string value, CardPlace place)
        {
            string label = WidgetSettings.CleanedLabel(value);
            Write("other device label (card)", s => s.Widget = s.Widget with { OtherDeviceLabel = label }, place);
        }

        public void SetPauseWhenBudComesOut(bool on, CardPlace place) =>
            Write("pause when a bud comes out (card)", s => s.Widget = (s.Widget with { AutoPause = on }).WithWatcherRecomputed(), place);

        public void SetPauseWhenAirPodsLeave(bool on, CardPlace place) =>
            Write("pause when the AirPods leave this PC (card)", s => s.PauseWhenAirPodsLeave = on, place);

        public void SetLowBatteryPercent(int percent, CardPlace place) =>
            Write("low battery threshold (card)", s => s.Widget = s.Widget with { LowBatteryThresholdPercent = percent }, place);

        public void SetLeftClickConnects(bool on, CardPlace place) =>
            Write("left click connects (card)", s => s.Widget = s.Widget with { LeftClickConnects = on }, place);

        // The menu tick's own path: refused while a session end or a hand-back is running, and carried to the
        // service's configuration once saved.
        public void SetHandBack(bool on, CardPlace place)
        {
            if (_tray._closing || _tray.RefusedAtSessionEnd("hand back at shut down and sleep", TrayStatus.AppName, place))
            {
                return;
            }

            if (_tray.TryUpdateSettings("hand back at shut down and sleep (card)", s => s.HandBackOnShutdownAndSleep = on, place))
            {
                _tray.MirrorHandBackSetting(on, place);
            }
        }

        public void SetHandsFreeMicrophoneOff(bool on, CardPlace place) => _tray.SetHandsFreeMicrophoneOff(on, place);

        public void OpenSoundSettings(CardPlace place) => _tray.OpenSoundSettings(place);

        public void SetCheckAutomatically(bool on, CardPlace place) =>
            Write("check for updates automatically (card)", s => s.CheckForUpdatesAutomatically = on, place);

        private static HotkeyAction ActionOf(CardShortcut shortcut) =>
            shortcut switch
            {
                CardShortcut.Connect => HotkeyAction.SwitchToPc,
                CardShortcut.Disconnect => HotkeyAction.SwitchToPhone,
                _ => HotkeyAction.OpenCard,
            };

        public string? SetShortcut(CardShortcut shortcut, Keys key, bool control, bool alt, bool shift, CardPlace place)
        {
            if (_tray._closing)
            {
                return null;
            }

            // The keys as a chord, in the words shortcuts are stored in. A key with no name in the shortcut table is
            // not one this build can register.
            HotkeyAction action = ActionOf(shortcut);
            ushort virtualKey = (ushort)(int)(key & Keys.KeyCode);
            if (!VirtualKeyTable.TryGetKeyName(virtualKey, out _))
            {
                return "That key cannot be used in a shortcut.";
            }

            HotkeyModifiers modifiers = (control ? HotkeyModifiers.Control : HotkeyModifiers.None)
                | (alt ? HotkeyModifiers.Alt : HotkeyModifiers.None)
                | (shift ? HotkeyModifiers.Shift : HotkeyModifiers.None);
            string chord = HotkeyText.Format(new HotkeyCombination(modifiers, virtualKey));

            // Checked on a copy first, so a refused chord changes nothing and saves nothing.
            HotkeySettings scratch = CopyOf(_tray._registry.Settings.Current.Hotkeys);
            string? refused = _tray.HotkeyBindings(scratch).Set(action, chord);
            if (refused is not null)
            {
                return refused;
            }

            bool saved = _tray.TryUpdateSettings("shortcut (card)", s => _tray.HotkeyBindings(s.Hotkeys).Set(action, chord), place);
            return saved ? null : WidgetCopy.ShortcutNotSaved;
        }

        public void ClearShortcut(CardShortcut shortcut, CardPlace place)
        {
            HotkeyAction action = ActionOf(shortcut);
            Write("clear shortcut (card)", s => _tray.HotkeyBindings(s.Hotkeys).Clear(action), place);
        }

        public SetupViewModel UpdatePage(int spinnerFrame) =>
            _tray.EnsureUpdates()?.View is { } view ? WidgetCardUpdatePage.From(view, spinnerFrame) : WidgetCardUpdatePage.Unavailable();

        public string? AvailableUpdateVersion() => _tray._updates?.AvailableRelease?.Version.ToString();

        public void CheckForUpdates() => _tray.CheckForUpdatesFromCard();

        public void StartUpdate() => _tray.StartUpdate();

        public void CancelUpdate() => _tray._updates?.Cancel();

        public void TryUpdateAgain() => _tray.TryUpdateAgainFromCard();

        public void SetUpEarshot() => _tray.SetUpFromCard();

        public void RepairEarshot() => _tray.RepairFromCard();

        public void SwitchToInstalled() => _tray.SwitchToInstalledCopy();

        private void Write(string what, Action<EarshotSettings> mutate, CardPlace place)
        {
            if (_tray._closing)
            {
                return;
            }

            _tray.TryUpdateSettingsFromWidget(what, mutate, place);
        }
    }
}

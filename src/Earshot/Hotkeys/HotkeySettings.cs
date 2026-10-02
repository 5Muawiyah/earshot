using System.Text.Json.Serialization;

namespace Earshot.Hotkeys;

// The owner's global keyboard shortcuts, one per HotkeyAction. Nested in EarshotSettings as Hotkeys and
// persisted with it (Earshot.Infra.SettingsJsonContext already reaches this type through EarshotSettings, so it
// needs no source-generation attribute of its own).
//
// Defaults: shortcuts on, with only the two directional ones typed: Ctrl+Alt+Shift+A connects and
// Ctrl+Alt+Shift+D disconnects. The other four commands stay empty until the owner types a combination. Three
// modifiers keep the defaults away from the combinations other programs commonly hold. The texts are stored
// exactly as the owner typed them, not canonicalised on save, so the settings file still shows what they wrote if
// HotkeyText.TryParse later rejects it; the canonical form from HotkeyText.Format is only used in messages.
//
// Settings files written before the two directional shortcuts existed (see OnDeserialized). System.Text.Json calls
// a property's setter only for a member the file contains, so a file without SwitchToPc or SwitchToPhone keeps the
// default for it, and a file that has the member, empty included, keeps what the owner left. A chord the owner
// already typed is never replaced. The one thing a default cannot do alone is Enabled: every file the previous
// build wrote carries "Enabled": false, typed or not, so that value says nothing about a wish when no chord was
// typed. Only then, and only while the file has neither new member, is it read as on. Once the file is saved it
// has both members and this never runs again, so an owner who later turns shortcuts off keeps them off.
//
// OpenCard (Ctrl+Alt+Shift+E) was added after those two and follows the same rule for the member alone: a file
// without it takes the default, a file with it, empty included, keeps what the owner left, so a cleared card
// shortcut stays cleared once the file is saved. It does not infer Enabled: a file that already has the switch
// members was written by a build that expressed the owner's wish for it. A typed chord the same as the default
// keeps it and the default steps aside, as above.
public sealed class HotkeySettings : IJsonOnDeserialized
{
    public const string DefaultSwitchToPc = "Ctrl+Alt+Shift+A";

    public const string DefaultSwitchToPhone = "Ctrl+Alt+Shift+D";

    private string _switchToPc = DefaultSwitchToPc;
    public const string DefaultOpenCard = "Ctrl+Alt+Shift+E";

    private string _switchToPhone = DefaultSwitchToPhone;
    private string _openCard = DefaultOpenCard;
    private bool _newMembersInFile;
    private bool _openCardInFile;

    public bool Enabled { get; set; } = true;

    public string ToggleConnection { get; set; } = string.Empty;

    public string ToggleAudioProtection { get; set; } = string.Empty;

    public string ToggleBlockAtBoot { get; set; } = string.Empty;

    public string SpeakStatus { get; set; } = string.Empty;

    public string SwitchToPc
    {
        get => _switchToPc;
        set
        {
            _switchToPc = value;
            _newMembersInFile = true;
        }
    }

    public string SwitchToPhone
    {
        get => _switchToPhone;
        set
        {
            _switchToPhone = value;
            _newMembersInFile = true;
        }
    }

    public string OpenCard
    {
        get => _openCard;
        set
        {
            _openCard = value;
            _openCardInFile = true;
        }
    }

    public static HotkeySettings Defaults => new();

    // The text for an action, or an empty string. Never null.
    public string TextFor(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleConnection => ToggleConnection,
        HotkeyAction.ToggleAudioProtection => ToggleAudioProtection,
        HotkeyAction.ToggleBlockAtBoot => ToggleBlockAtBoot,
        HotkeyAction.SpeakStatus => SpeakStatus,
        HotkeyAction.SwitchToPc => SwitchToPc,
        HotkeyAction.SwitchToPhone => SwitchToPhone,
        HotkeyAction.OpenCard => OpenCard,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown hotkey action."),
    };

    // Sets the text for an action: the one place a caller writes what TextFor reads.
    public void SetText(HotkeyAction action, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (action)
        {
            case HotkeyAction.ToggleConnection:
                ToggleConnection = text;
                break;
            case HotkeyAction.ToggleAudioProtection:
                ToggleAudioProtection = text;
                break;
            case HotkeyAction.ToggleBlockAtBoot:
                ToggleBlockAtBoot = text;
                break;
            case HotkeyAction.SpeakStatus:
                SpeakStatus = text;
                break;
            case HotkeyAction.SwitchToPc:
                SwitchToPc = text;
                break;
            case HotkeyAction.SwitchToPhone:
                SwitchToPhone = text;
                break;
            case HotkeyAction.OpenCard:
                OpenCard = text;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown hotkey action.");
        }
    }

    // Runs after a file has been read, never for a new object. See the class comment for why this is the whole
    // of the upgrade.
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!_newMembersInFile)
        {
            UpgradeSwitchMembers();
        }

        if (!_openCardInFile &&
            HotkeyText.TryParse(_openCard, out HotkeyCombination card, out _) &&
            HeldByAnother(card))
        {
            _openCard = string.Empty;
        }
    }

    // True when another command, as read, already holds the combination.
    private bool HeldByAnother(HotkeyCombination combination)
    {
        HotkeyAction[] others =
        [
            HotkeyAction.ToggleConnection,
            HotkeyAction.ToggleAudioProtection,
            HotkeyAction.ToggleBlockAtBoot,
            HotkeyAction.SpeakStatus,
            HotkeyAction.SwitchToPc,
            HotkeyAction.SwitchToPhone,
        ];
        foreach (HotkeyAction action in others)
        {
            if (HotkeyText.TryParse(TextFor(action), out HotkeyCombination held, out _) && held == combination)
            {
                return true;
            }
        }

        return false;
    }

    private void UpgradeSwitchMembers()
    {
        bool oldTextsEmpty = string.IsNullOrWhiteSpace(ToggleConnection) &&
                             string.IsNullOrWhiteSpace(ToggleAudioProtection) &&
                             string.IsNullOrWhiteSpace(ToggleBlockAtBoot) &&
                             string.IsNullOrWhiteSpace(SpeakStatus);
        if (!Enabled && oldTextsEmpty)
        {
            Enabled = true;
        }

        // A default that another of the owner's own commands already holds is left empty rather than reported as a
        // clash the owner did not cause.
        HotkeyAction[] typed =
        [
            HotkeyAction.ToggleConnection,
            HotkeyAction.ToggleAudioProtection,
            HotkeyAction.ToggleBlockAtBoot,
            HotkeyAction.SpeakStatus,
        ];
        foreach (HotkeyAction action in typed)
        {
            if (!HotkeyText.TryParse(TextFor(action), out HotkeyCombination held, out _))
            {
                continue;
            }

            if (HotkeyText.TryParse(_switchToPc, out HotkeyCombination pc, out _) && pc == held)
            {
                _switchToPc = string.Empty;
            }

            if (HotkeyText.TryParse(_switchToPhone, out HotkeyCombination phone, out _) && phone == held)
            {
                _switchToPhone = string.Empty;
            }
        }
    }
}

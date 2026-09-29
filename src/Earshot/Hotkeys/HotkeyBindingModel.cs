namespace Earshot.Hotkeys;

// What a settings page binds one shortcut box to: the chord as it stands, changing it, clearing it, and whether the
// last registration of it failed. It edits a HotkeySettings the page owns (the copy handed to Settings.Update) and
// reads the outcomes the running HotkeyManager last produced, so the page never talks to Windows itself.
public sealed class HotkeyBindingModel
{
    private readonly HotkeySettings _settings;
    private readonly Func<IReadOnlyList<HotkeyRegistrationOutcome>> _outcomes;

    public HotkeyBindingModel(HotkeySettings settings, Func<IReadOnlyList<HotkeyRegistrationOutcome>> outcomes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(outcomes);
        _settings = settings;
        _outcomes = outcomes;
    }

    // The chord as stored, "" when there is none.
    public string Chord(HotkeyAction action) => _settings.TextFor(action);

    // Stores a chord when it is one this build would register: it parses, is not reserved by Windows and is not
    // already set for another command. Returns null on success, or the reason in the owner's words, and stores
    // nothing on a refusal so a half-typed box never overwrites a working shortcut. The text is stored as typed.
    public string? Set(HotkeyAction action, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!HotkeyText.TryParse(text, out HotkeyCombination combination, out string error))
        {
            return error;
        }

        if (HotkeyManager.IsReserved(combination))
        {
            return HotkeyManager.F12RefusedMessage;
        }

        foreach (HotkeyAction other in Enum.GetValues<HotkeyAction>())
        {
            if (other != action &&
                HotkeyText.TryParse(_settings.TextFor(other), out HotkeyCombination held, out _) &&
                held == combination)
            {
                return HotkeyText.Format(combination) + " is already set for another command here.";
            }
        }

        _settings.SetText(action, text);
        return null;
    }

    // Leaves the command with no shortcut. The empty text is kept, so a cleared default stays cleared.
    public void Clear(HotkeyAction action) => _settings.SetText(action, string.Empty);

    // True when the last Apply tried to register this chord and Windows or the settings refused it: held by another
    // program, text that does not parse, a clash between two commands, or another Windows error. False when it
    // registered, when there is no chord, when shortcuts are switched off, and before the first Apply.
    public bool RegistrationFailed(HotkeyAction action) => FailureMessage(action) is not null;

    // The outcome's own words for a failure, or null.
    public string? FailureMessage(HotkeyAction action)
    {
        foreach (HotkeyRegistrationOutcome outcome in _outcomes())
        {
            if (outcome.Action == action)
            {
                return outcome.State is HotkeyRegistrationState.NotSet or HotkeyRegistrationState.Registered ? null : outcome.Message;
            }
        }

        return null;
    }
}

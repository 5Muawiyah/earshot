namespace Earshot.Hotkeys;

public enum HotkeyRegistrationState
{
    NotSet,
    Registered,
    TextRejected,
    Refused,
    DuplicateInSettings,
    AlreadyHeld,
    Failed,
}

namespace Earshot.Voice;

// The closed set of things Earshot may say out loud. No member carries data: VoicePhrases.For is the
// only place a VoiceLine becomes text, and Announce(VoiceLine) is the only way into the speech worker,
// so a device name, a Bluetooth address or a number can never reach the synthesiser (see AnnouncerCopy
// and SpeechAnnouncer).
public enum VoiceLine
{
    Connected,
    Disconnected,
    BlockedAtBoot,
    AllowedAtBoot,
    ConnectFailed,
    BlockFailed,
}

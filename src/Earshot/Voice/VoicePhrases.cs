namespace Earshot.Voice;

// The fixed phrase table. Every phrase is letters and single spaces only: no digit, no percent sign,
// no colon, no em-dash, because this product has no battery reading and no signal strength, so there is
// nothing numeric to say (see AnnouncerCopy and the voiceover design notes).
public static class VoicePhrases
{
    public static string For(VoiceLine line) => line switch
    {
        VoiceLine.Connected => "Connected",
        VoiceLine.Disconnected => "Disconnected",
        VoiceLine.BlockedAtBoot => "Blocked at boot",
        VoiceLine.AllowedAtBoot => "Allowed at boot",
        VoiceLine.ConnectFailed => "Connect failed",
        VoiceLine.BlockFailed => "Block failed",
        _ => throw new ArgumentOutOfRangeException(nameof(line), line, "Unknown voice line."),
    };

    public static bool IsFailure(VoiceLine line) => line is VoiceLine.ConnectFailed or VoiceLine.BlockFailed;
}

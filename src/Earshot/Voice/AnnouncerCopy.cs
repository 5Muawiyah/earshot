namespace Earshot.Voice;

// Every string a person reads or hears about VoiceOver, in one place. British English, plain, short, no
// em-dashes, no figures: the same house rules as the rest of Earshot's copy.
public static class AnnouncerCopy
{
    public const string MenuItem = "Speak status";
    public const string MenuItemNoVoice = "Speak status (no voice)";
    public const string NoVoiceInstalled = "No speech voice is installed, so Earshot cannot speak.";
    public const string SpeechStopped = "Speech stopped working, so Earshot has turned it off.";
    public const string SpeechOn = "Speech is on.";
    public const string SpeechOff = "Speech is off.";
}

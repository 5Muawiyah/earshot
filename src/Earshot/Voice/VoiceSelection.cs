namespace Earshot.Voice;

// The rule SelectVoice(String) itself documents: a case-sensitive substring match on VoiceInfo.Name,
// and it cannot select a voice whose Enabled is false.
// https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.selectvoice
// A pure function over the installed list, so it can be tested without constructing a real
// SpeechSynthesizer: SystemSpeechEngine calls this first and only ever calls SelectVoice with the exact
// installed name this returns, never with the owner's raw setting. That is what "do not call SelectVoice
// blind" means here: nothing is passed to the real API until this has proved a match exists.
internal static class VoiceSelection
{
    // Returns the exact installed name to pass to SelectVoice, or null when nothing enabled matches
    // (including when requested is null or empty, in which case the default voice is what Open already
    // uses and no lookup is needed). The first enabled installed voice whose Name contains requested,
    // ordinal and case-sensitive, wins, in the order installed lists them: the Microsoft page linked
    // above documents the match rule (case-sensitive substring, Enabled required) but not a search
    // order for more than one match, so this comment no longer claims one it cannot cite.
    public static string? Choose(string? requested, IReadOnlyList<(string Name, bool Enabled)> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (string.IsNullOrEmpty(requested))
        {
            return null;
        }

        foreach ((string name, bool enabled) in installed)
        {
            if (enabled && name.Contains(requested, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }
}

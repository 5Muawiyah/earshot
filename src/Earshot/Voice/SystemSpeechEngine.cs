using System.Speech.Synthesis;
using Earshot.Contracts;

namespace Earshot.Voice;

// The only file that touches System.Speech. Not thread-safe, and does not need to be: exactly one
// thread ever calls Open or Speak, the "voiceover" worker SpeechAnnouncer starts, by the design in
// SpeechAnnouncer's own header. Package: System.Speech (Microsoft-published, MIT licensed), the only
// first-party managed way to speak offline without moving the app to a Windows-version-specific
// TargetFramework (net10.0-windows10.0.19041.0 for the WinRT synthesiser). See Earshot.csproj.
// https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer
// Internal, along with ISpeechEngine (see that interface's own header): TrayContext.VoiceEngineFactory
// constructs this by name (App\TrayContext.cs) and needs no public access to do so, since it is in the
// same assembly.
internal sealed class SystemSpeechEngine : ISpeechEngine
{
    // An echoed VoiceName is data from a user-writable settings file, not something Earshot chose, so
    // it is bounded and sanitised the same way a rejected hotkey shortcut is echoed back
    // (Hotkeys\HotkeyText.cs, MaxEchoedSegmentLength) before it can reach the log.
    private const int MaxEchoedVoiceNameLength = 40;

    private SpeechSynthesizer? _synth;
    private bool _disposed;

    public bool IsOpen => _synth is not null;

    // The enabled voice names SystemSpeechEngine itself saw on the last Open, for the binding test to
    // compare against an independent probe (SystemSpeechEngineBindingTests). Not part of ISpeechEngine:
    // production code never reads it.
    internal IReadOnlyList<string> LastOpenEnabledVoiceNames { get; private set; } = Array.Empty<string>();

    // The voice actually in force after Open: the one SelectVoice picked, or the default voice Open left
    // in place. Read from the synthesiser itself, never cached, so it can never drift from reality. Null
    // once the engine is closed. Not part of ISpeechEngine: only the binding test needs it.
    internal string? SelectedVoiceName => _synth?.Voice.Name;

    public StepOutcome Open(VoiceOverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        SpeechSynthesizer synth;
        try
        {
            synth = new SpeechSynthesizer();
        }
        catch (Exception ex)
        {
            // The docs name no exception for construction failing; a broad catch here still records the
            // raw type and HRESULT rather than crashing the tray, which is what the no-silent-catch rule
            // asks for. Nothing native has been opened yet, so there is nothing to disunwind.
            return new StepOutcome("open", Ok: false, ex.HResult, ex.GetType().Name, ex.Message);
        }

        try
        {
            // GetInstalledVoices verifies each voice against minimum criteria and sets Enabled false for
            // any that fail. An empty collection, or every voice disabled, is a documented, ordinary
            // result, not an error, and is reported as NotAvailable rather than a failure.
            // https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.getinstalledvoices
            (string Name, bool Enabled)[] installed = synth.GetInstalledVoices()
                .Select(v => (v.VoiceInfo.Name, v.Enabled))
                .ToArray();
            LastOpenEnabledVoiceNames = installed.Where(v => v.Enabled).Select(v => v.Name).ToArray();
            bool anyUsable = LastOpenEnabledVoiceNames.Count > 0;
            if (!anyUsable)
            {
                synth.Dispose();
                return StepOutcomes.NotAvailable("open", "no enabled voice");
            }

            // Never call SelectVoice with the owner's raw setting: pick the exact installed name first,
            // by the rule SelectVoice(String) itself documents (case-sensitive substring match on
            // VoiceInfo.Name, and it cannot select a voice whose Enabled is false); only that verified
            // name, never the raw text, is ever handed to SelectVoice.
            // https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.selectvoice
            string? voiceNote = null;
            if (settings.VoiceName is { Length: > 0 } requested)
            {
                string? chosen = VoiceSelection.Choose(requested, installed);
                if (chosen is null)
                {
                    voiceNote = " Voice \"" + Echo(requested) + "\" not found; using the default voice.";
                }
                else
                {
                    try
                    {
                        synth.SelectVoice(chosen);
                    }
                    catch (Exception ex)
                    {
                        // The docs name no exception for SelectVoice either, and this machine has shown
                        // one they do not document (NullReferenceException from
                        // VoiceSynthesis.GetEngineWithVoice for a name that matches nothing; here the
                        // name is already known to match an enabled installed voice, but the same "no
                        // documented exception" reasoning applies, so the catch stays broad, records the
                        // raw type and HResult, and falls back to the default voice rather than failing
                        // Open).
                        voiceNote = " Voice \"" + Echo(requested) + "\" (" + ex.GetType().Name + ", " +
                            NativeCodes.Name(ex.HResult) + ") could not be selected; using the default voice.";
                    }
                }
            }

            synth.Rate = Math.Clamp(settings.Rate, -10, 10);
            synth.Volume = Math.Clamp(settings.Volume, 0, 100);
            synth.SetOutputToDefaultAudioDevice();
            _synth = synth;
            return StepOutcomes.FromHResult("open", 0, voiceNote);
        }
        catch (Exception ex)
        {
            synth.Dispose();
            return new StepOutcome("open", Ok: false, ex.HResult, ex.GetType().Name, ex.Message);
        }
    }

    // Bounds and sanitises a VoiceName before it can reach the log: it is read from a user-writable
    // settings file, not chosen by Earshot, the same reasoning HotkeyText.Echo applies to a rejected
    // shortcut (Hotkeys\HotkeyText.cs).
    private static string Echo(string name)
    {
        string oneLine = name.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var clean = new System.Text.StringBuilder(oneLine.Length);
        foreach (char c in oneLine)
        {
            if (!char.IsControl(c))
            {
                clean.Append(c);
            }
        }

        string sanitised = clean.ToString();
        return sanitised.Length <= MaxEchoedVoiceNameLength
            ? sanitised
            : sanitised[..MaxEchoedVoiceNameLength] + "...";
    }

    public StepOutcome Speak(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_synth is not { } synth)
        {
            return StepOutcomes.NotAttempted("speak", "engine not open");
        }

        try
        {
            // Speak(String), never SpeakAsync: SpeakAsync methods "return immediately without waiting
            // for the content ... to finish speaking", and SpeakAsyncCancelAll "Cancels all queued,
            // asynchronous, speech synthesis operations", which is the documented evidence for an
            // internal queue of unstated length, exactly what the coalescing design in SpeechAnnouncer
            // must not rely on.
            // https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.speakasync
            // Blocking here is correct: the caller is the dedicated worker thread, and Speak(String)
            // itself is documented here:
            // https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.speak
            synth.Speak(text);
            return StepOutcomes.FromHResult("speak", 0);
        }
        catch (Exception ex)
        {
            // No exception type is documented for Speak, so this is deliberately broad: recording the
            // raw type and HRESULT and returning is what stops the tray crashing, not narrowing the catch
            // to types nobody has documented.
            return new StepOutcome("speak", Ok: false, ex.HResult, ex.GetType().Name, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // "Always call Dispose before you release your last reference to the SpeechSynthesizer."
        // https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer
        _synth?.Dispose();
        _synth = null;
    }
}

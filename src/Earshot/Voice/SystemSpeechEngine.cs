using System.Speech.Synthesis;
using Earshot.Contracts;

namespace Earshot.Voice;

// The only file that touches System.Speech. Not thread-safe, and does not need to be: exactly one
// thread ever calls Open or Speak, the "voiceover" worker SpeechAnnouncer starts, by the design in
// SpeechAnnouncer's own header. Package: System.Speech (Microsoft-published, MIT licensed), the only
// first-party managed way to speak offline without moving the app to a Windows-version-specific
// TargetFramework (net10.0-windows10.0.19041.0 for the WinRT synthesiser). See Earshot.csproj.
// https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer
public sealed class SystemSpeechEngine : ISpeechEngine
{
    private SpeechSynthesizer? _synth;
    private bool _disposed;

    public bool IsOpen => _synth is not null;

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
            bool anyUsable = synth.GetInstalledVoices().Any(v => v.Enabled);
            if (!anyUsable)
            {
                synth.Dispose();
                return StepOutcomes.NotAvailable("open", "no enabled voice");
            }

            string? voiceNote = null;
            if (settings.VoiceName is { Length: > 0 } name)
            {
                try
                {
                    // Case-sensitive substring match on VoiceInfo.Name; cannot select a voice whose
                    // Enabled is false. https://learn.microsoft.com/en-us/dotnet/api/system.speech.synthesis.speechsynthesizer.selectvoice
                    synth.SelectVoice(name);
                }
                catch (ArgumentException ex)
                {
                    // Recorded here, not swallowed: the failure rides in the Open outcome's Detail, and
                    // the default voice is used instead, exactly as the design asks.
                    voiceNote = " Voice \"" + name + "\" (" + ex.GetType().Name + ") not found; using the default voice.";
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
            // Speak(String), never SpeakAsync: SpeakAsync keeps its own internal queue of unstated
            // length, which is exactly what the coalescing design in SpeechAnnouncer must not rely on.
            // Blocking here is correct: the caller is the dedicated worker thread.
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

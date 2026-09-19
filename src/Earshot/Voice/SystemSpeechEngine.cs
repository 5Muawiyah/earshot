using System.Speech.Synthesis;
using Earshot.Contracts;

namespace Earshot.Voice;

// The only file that touches System.Speech. Not thread-safe, and does not need to be: exactly one
// thread ever calls Open or Speak, the "voiceover" worker SpeechAnnouncer starts, by the design in
// SpeechAnnouncer's own header. Package: System.Speech (Microsoft-published, MIT licensed): when
// voiceover was built, this was the only first-party managed way to speak offline without moving the
// app to a Windows-version-specific TargetFramework (net10.0-windows10.0.19041.0, for the WinRT
// synthesiser). Audio streaming has since moved the app to that TargetFramework anyway
// (Directory.Build.props; see Earshot.csproj's own note on the System.Speech reference), and this file
// was not revisited then: System.Speech is still what speaks. See Earshot.csproj.
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
    //
    // Guarded: reading SpeechSynthesizer.Voice has shown NullReferenceException on this machine, from
    // System.Speech.Internal.Synthesis.VoiceSynthesis.GetEngineWithVoice, undocumented and reached
    // through the property getter rather than a method (Microsoft's Voice page names no exception
    // either, the same "no documented exception" class Open's own SelectVoice catch exists for). This
    // property exists only for a binding test to read, with no StepOutcome to record a failure into, so
    // null is the answer: it is already what this property gives once the engine is closed, and "which
    // voice is selected could not be read" is the honest reading of that, not "no voice at all".
    internal string? SelectedVoiceName
    {
        get
        {
            if (_synth is not { } synth)
            {
                return null;
            }

            try
            {
                return synth.Voice.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

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
            int? selectFailureHResult = null;
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
                        // documented exception" reasoning applies, so the catch stays broad, falls back to
                        // the default voice rather than failing Open, and records the raw type and
                        // HResult). The raw HResult also goes into the returned StepOutcome's own Code
                        // below, a structured field, not only decoded into this detail sentence: a caller
                        // that wants the exact code back does not have to parse it out of English text.
                        selectFailureHResult = ex.HResult;
                        voiceNote = " Voice \"" + Echo(requested) + "\" (" + ex.GetType().Name + ", " +
                            NativeCodes.Name(ex.HResult) + ") could not be selected; using the default voice.";
                    }
                }
            }

            synth.Rate = Math.Clamp(settings.Rate, -10, 10);
            synth.Volume = Math.Clamp(settings.Volume, 0, 100);
            synth.SetOutputToDefaultAudioDevice();
            _synth = synth;

            // Open still succeeds (Ok stays true: the default voice is in force either way), but when
            // SelectVoice threw, Code and CodeName carry exactly what it threw rather than the usual 0 /
            // S_OK a successful "open" reports; StepOutcomes.FromHResult's own ok parameter is what lets
            // Code disagree with Ok this way.
            return selectFailureHResult is int hr
                ? StepOutcomes.FromHResult("open", hr, voiceNote, ok: true)
                : StepOutcomes.FromHResult("open", 0, voiceNote);
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
        // A space, not string.Empty: deleting CR/LF outright can run the words either side of it
        // together ("Hello\r\nWorld" becoming "HelloWorld", changing what the log actually shows), and a
        // fake timestamped line folded into the same log entry this way could otherwise be misread as a
        // second, genuine one. A space keeps the words apart and keeps this echo to the one line it must
        // stay on.
        string oneLine = name.Replace("\r", " ").Replace("\n", " ");
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

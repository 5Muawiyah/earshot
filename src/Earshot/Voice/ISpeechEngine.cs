using Earshot.Contracts;

namespace Earshot.Voice;

// The only thing that touches the speech engine. Blocking is allowed here, because only the VoiceOver
// worker thread that SpeechAnnouncer owns ever calls it (see SpeechAnnouncer's own header). Every step
// returns a StepOutcome, the same outcome family every other native call in Earshot uses (Contracts\
// Results.cs, Contracts\StepOutcomes.cs), so speech failures are recorded and decoded exactly like a
// CfgMgr32 or Bluetooth failure: no bool that loses the reason, no silent catch.
//
// Internal, not public: Speak(string) takes a free string, and VoiceLine.cs claims no free string can
// reach the synthesiser. That claim only holds inside this assembly if nothing outside it can call
// Speak directly, which internal (with InternalsVisibleTo Earshot.Tests, Earshot.csproj) enforces; a
// public interface here would let any caller outside Earshot speak arbitrary text through it, whatever
// VoiceLine.cs says. TrayContext (the only production caller) and the test project both reach it fine
// through internal visibility.
internal interface ISpeechEngine : IDisposable
{
    bool IsOpen { get; }

    // Creates the engine, picks a voice and sets the output. Returns StepOutcomes.NotAvailable("open", ...)
    // when there is no enabled voice on this machine, which is a documented, ordinary result and must not
    // be reported as a failure of anything.
    StepOutcome Open(VoiceOverSettings settings);

    // Speaks text and returns once it has finished. Blocking is correct and expected: see the threading
    // note on SpeechAnnouncer for why.
    StepOutcome Speak(string text);
}

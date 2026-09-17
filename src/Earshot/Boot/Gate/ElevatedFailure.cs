using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// The step an elevated mode records when something it did not expect stopped it. The exception's HResult keeps
// the native code where there is one (a COMException, an IOException), so the failure is named rather than
// lost; the mode then ends with GateExitCode.Failed and the steps it had already taken.
internal static class ElevatedFailure
{
    public const string StepSuffix = "-stopped";

    public static StepOutcome Step(string what, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(error);
        return StepOutcomes.FromHResult(what + StepSuffix, error.HResult, error.GetType().Name + ": " + error.Message, ok: false);
    }
}

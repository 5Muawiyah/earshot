using Earshot.Widget;

namespace Earshot.Tests.Phase1;

// What the tray told the one card about a failure: the reason, and the next step's words and what pressing it does, or none.
internal sealed record FailureSaid(string Reason, string? NextLabel, Action? Press, bool MustBeSaid = false);

// Stands in for the one card's side of a toggle: records what the tray told it, and answers what became of a failure.
internal sealed class RecordingToggleCard : IToggleCard
{
    public List<bool> Started { get; } = new();

    public List<(bool Connect, ToggleOutcome Outcome)> Ended { get; } = new();

    public List<FailureSaid> Failures { get; } = new();

    // What ShowFailed says became of the failure: a card said it, by default.
    public FailureShown Answer { get; set; } = FailureShown.Shown;

    public void ToggleStarted(bool connect) => Started.Add(connect);

    public void ToggleEnded(bool connect, ToggleOutcome outcome) => Ended.Add((connect, outcome));

    public Task<FailureShown> ShowFailed(string reason, FailureNextStep? nextStep, bool mustBeSaid = false, bool afterToggle = true)
    {
        Failures.Add(new FailureSaid(reason, nextStep?.Label, nextStep?.OnPress, mustBeSaid));
        return Task.FromResult(Answer);
    }
}

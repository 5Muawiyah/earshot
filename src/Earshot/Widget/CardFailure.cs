namespace Earshot.Widget;

// What the card's Connect or Disconnect button is doing: nothing, or running a connect or a disconnect. While it runs the label
// gives way to a ring and a press is ignored.
internal enum CardButtonWork { None, Connecting, Disconnecting }

// The one thing a failure line offers to do next ("Connect in Windows", "Fix connection", a radio's button), and what pressing it
// does. The card hides the failure when it is pressed and then runs OnPress.
internal sealed record FailureNextStep(string Label, Action OnPress);

// What became of a failure handed to the card.
internal enum FailureShown
{
    // A card was open, or one opened now, and says it.
    Shown,

    // The card opens when the gauge's cross has had its time; it is checked again then.
    Pending,

    // A gate refused the card for now: the failure is held and shown on the next card he asks for, until the state changes.
    Held,

    // Nothing to show: no card is open and there is no next step to offer. The gauge's cross and its tooltip say it.
    NotShown,
}

// What the tray tells the card about a connect or a disconnect, and the failure of one. The one presenter implements it.
internal interface IToggleCard
{
    // A connect or a disconnect began: the button turns to a ring, and a failure from the last attempt goes.
    void ToggleStarted(bool connect);

    // It ended, whatever way: the ring goes. Called once for every ToggleStarted.
    void ToggleEnded(bool connect, ToggleOutcome outcome);

    // The attempt failed: reason is the whole sentence, nextStep the one button that can help, or null. mustBeSaid is a failure that
    // leaves the PC able to page the AirPods (a disconnect whose block did not take): with no card open and no next step it is held
    // for the next card he asks for instead of being left to the gauge's cross, which may never have been on screen. afterToggle is false
    // for a failure no connect or disconnect led to (Pair AirPods), so it is not named after the last toggle, nor told as its cross.
    Task<FailureShown> ShowFailed(string reason, FailureNextStep? nextStep, bool mustBeSaid = false, bool afterToggle = true);
}

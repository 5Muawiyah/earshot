using System.Drawing;
using Earshot.Contracts;

namespace Earshot.App;

// Where the cards for one operation go. A card that follows the user's own click is anchored at the point
// the cursor was at when the click happened, read before anything was awaited; any other card goes near the
// notification area.
// https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
internal readonly record struct CardPlace(CardAnchor Anchor, Point? ClickPoint)
{
    public static CardPlace NearTray => new(CardAnchor.NearTray, null);

    // A card that follows the user's action when no click point is known (a second copy of Earshot started).
    public static CardPlace NearCursor => new(CardAnchor.NearCursor, null);

    public static CardPlace AtClick(Point clickPoint) => new(CardAnchor.NearCursor, clickPoint);

    public void Show(ICardPresenter cards, string title, string status)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var content = new CardContent(title, status);
        if (ClickPoint is { } point)
        {
            cards.Show(content, Anchor, point);
        }
        else
        {
            cards.Show(content, Anchor);
        }
    }

    // Shows the card and completes with whether it was put on screen, for a card that is remembered once seen.
    public Task<bool> ShowAsync(ICardPresenter cards, string title, string status)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards.ShowAsync(new CardContent(title, status), Anchor, ClickPoint);
    }
}

// A connect or disconnect handed to the block coordinator: which container, the name for its cards and
// where those cards go.
internal sealed record ToggleRequest(bool Connect, Guid Container, string DeviceName, CardPlace Place);

// How a connect or disconnect ended, after every clean-up it needed.
//   Status       Success when the wanted state was observed; Partial when it was, but a follow-up (protection
//                re-applied, nodes blocked) did not work; Failed otherwise
//   UserMessage  the last card the operation showed, or what it would have said
//   Steps        every native step, in order: connect, allow, block and protection
//   Cancelled    the caller's token or the coordinator itself cancelled it; the clean-up still ran before the
//                report was returned
//   CancelledBecause  why the coordinator cancelled it (the session is ending, Earshot is closing), or null when
//                it was not cancelled or only the caller's token was, whose reason the caller knows
internal sealed record ToggleReport(bool Connect, OpStatus Status, string UserMessage, IReadOnlyList<StepOutcome> Steps, bool Cancelled)
{
    public string? CancelledBecause { get; init; }

    public bool IsSuccess => Status is OpStatus.Success or OpStatus.AlreadyInState;
}

// How the block coordinator was started.
//   SafeMode        EARSHOT_SAFE_MODE: every live action is refused by the Safe decorators, so no progress card
//                   is shown for an action that cannot follow
//   StartedAtLogon  the tray was started by its Run value (--startup), so render ACTIVE at the first check is
//                   evidence that the boot block did not hold
internal sealed record CoordinatorOptions(bool SafeMode, bool StartedAtLogon);

// Why a Block sequence runs, which decides what is checked again just before the block is sent.
internal enum BlockReason
{
    Disconnect,       // the user's disconnect: blocks unless Block at boot is now off
    ConnectCleanUp,   // a connect that did not reach ACTIVE undoes its allow, unless the AirPods are in use after all
    DeviceChangeCleanUp, // a device change whose pin did not move undoes its allow, unless the AirPods are in use
    Idle,             // the idle rule: needs good reads of the nodes and the endpoints
    StartUp,          // the start-up check: the same
    Closing,          // the block before Earshot closes: the same
    HandBack,         // the hand-back at shut down or sleep: never cancelled, no protection step
    Resume,           // the resume check after sleep: the start-up check under another name
}

// What raised the hand-back: WM_ENDSESSION (shut down, restart or sign-out) or PBT_APMSUSPEND (sleep). Each
// carries its own budget and its own wording for the log lines.
internal enum HandBackTrigger
{
    SessionEnd,
    Suspend,
}

// Why the gate refused to move the pin, where the device change can do something about it.
internal enum SetDeviceRefusal
{
    None,                // a success, or a refusal nothing here can change (not an audio device, not found, busy)
    OldDeviceBlocked,    // the device pinned now still has a disabled node
    OldDeviceProtected,  // protection.json lists services turned off on the device pinned now
}

// The render side of the device as one snapshot observed it.
internal enum RenderState
{
    Unknown,    // nothing to act on: the enumeration failed or has not run, or no device container is known
    Active,     // a render endpoint in the container is ACTIVE: the AirPods are in use on this PC
    NotActive,  // the enumeration worked and no render endpoint in the container is ACTIVE
}

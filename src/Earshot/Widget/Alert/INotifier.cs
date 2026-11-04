using Earshot.Contracts;

namespace Earshot.Widget.Alert;

// The compact row an alert shows when it has to be drawn by the app: the glyph, the ink and the words of the low battery and fully
// charged rows (CompactContents), and what it is about. A toast shows the title and the text instead.
internal sealed record CompactLine(CompactContent Content)
{
    // "Left 20%", "Right 20%", "Case 10%", "Left and Right 20%" (the lower of the two), "AirPods 20%" for the headset's figure.
    public static CompactLine Low(string partsLabel, int percent) => new(CompactContents.LowBattery(partsLabel, percent));

    public static CompactLine FullyCharged() => new(CompactContents.FullyCharged());
}

// Shows an alert. ToastNotifier is the real route; CompactAlertNotifier is both the fallback when a toast cannot be shown and the
// whole notifier in safe mode or with a redirected data root. line is the compact row for the notifier that draws one; a notifier
// that shows a toast uses title and text.
internal interface INotifier
{
    Task<StepOutcome> NotifyAsync(string title, string text, CompactLine? line = null);
}

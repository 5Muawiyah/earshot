namespace Earshot.Widget;

// What the card says about the battery beyond the three columns: the read line, and the ink a part that is not
// fresh is drawn in. The values themselves come from the model's ShownParts (BatteryFreshness), so a part's age
// is decided in one place and the card only picks a colour.
internal sealed partial class WidgetCard
{
    // How much of its colour a value that is not fresh keeps. Greyed, not hidden: the figure is still the last one
    // read, and the read line says how old it is.
    private const float StaleInkOpacity = 0.55f;

    // The line under the where line: Windows' own figure when that is what is shown ("Windows reads 70%"), else
    // when the battery was last read ("Battery read 4 min ago"). The age is the oldest part's, so one part going
    // stale shows in the line as well as in the part.
    internal string ReadLineText =>
        _model.ShownParts.WindowsPercent is int figure
            ? WidgetCopy.WindowsReads(figure)
            : WidgetCopy.BatteryReadLine(_model.Snapshot.BatteryReadAt, _model.Now);

    private static Color MutedInk(Color ink) => Color.FromArgb((int)Math.Round(ink.A * StaleInkOpacity), ink);
}

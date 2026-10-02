using System.Globalization;
using Earshot.Interop;
using Earshot.Popup;

namespace Earshot.Widget;

// When the case-open card closes by itself, as WidgetSettings.CaseOpenCardCloseSeconds stores it. The five choices are the
// owner's (2 October 2026). Pure.
internal static class CaseOpenCardClose
{
    // The default: the card stays until the case closes (CaseOpenTracker decides that from the messages).
    public const int UntilCaseCloses = 0;

    // Every stored value the setting offers, in the order the settings row steps through them. Built element by element:
    // the widget's own at-rest test reads every widget type's IL, and a constant array initialiser reads a compiler-made
    // data field it cannot vouch for.
    public static readonly IReadOnlyList<int> Choices = BuildChoices();

    private static List<int> BuildChoices()
    {
        var list = new List<int>(5);
        list.Add(UntilCaseCloses);
        list.Add(5);
        list.Add(10);
        list.Add(30);
        list.Add(60);
        return list;
    }

    public static bool IsChoice(int seconds) => Choices.Contains(seconds);

    // The time after which the card closes by itself, or null when it waits for the case to close.
    public static TimeSpan? After(int seconds) =>
        IsChoice(seconds) && seconds != UntilCaseCloses ? TimeSpan.FromSeconds(seconds) : null;

    // The choice after the current one, wrapping round; the first when the current one is not a choice.
    public static int Next(int seconds)
    {
        for (int i = 0; i < Choices.Count; i++)
        {
            if (Choices[i] == seconds)
            {
                return Choices[(i + 1) % Choices.Count];
            }
        }

        return Choices[0];
    }

    public static string Label(int seconds) =>
        IsChoice(seconds) && seconds != UntilCaseCloses
            ? seconds.ToString(CultureInfo.InvariantCulture) + " s"
            : WidgetCopy.CaseCardUntilCaseCloses;
}

// Which displays show the case-open card, as WidgetSettings.CaseOpenCardDisplays stores it: empty for where the gauge is,
// All alone for every display, or the identities of a set of displays. Pure.
internal static class CaseOpenCardDisplayChoice
{
    // The stored value for every display. A display's Id is a device interface path (it starts with "\\?\") or "gdi:"
    // and a device name, so a value that starts with "*" can never be one; the gauge's own All displays value is reused.
    public const string All = GaugeDisplayChoice.AllDisplays;

    // A design bound, not a platform limit: more identities than any desk has displays is not a choice.
    public const int MaxDisplays = 16;

    public static bool IsWhereTheGaugeIs(IReadOnlyList<string>? stored) => stored is null || stored.Count == 0;

    public static bool IsAll(IReadOnlyList<string>? stored) => stored is { Count: > 0 } && stored.Contains(All, StringComparer.Ordinal);

    // The stored list with anything that cannot be a display identity left out (empty, control characters, longer than the
    // gauge display's own bound), duplicates dropped, and All alone when it is there at all. The same array when nothing
    // changed, so a setting read back unchanged is not reported as cleaned.
    public static string[] Cleaned(string[]? stored, out bool changed)
    {
        if (stored is null)
        {
            changed = true;
            return [];
        }

        if (stored.Length == 0)
        {
            // The one shared empty array, so a setting read back empty equals the default (record equality compares an
            // array by reference).
            changed = false;
            return [];
        }

        if (IsAll(stored))
        {
            changed = stored.Length != 1;
            return changed ? [All] : stored;
        }

        var kept = new List<string>(stored.Length);
        foreach (string? raw in stored)
        {
            string id = (raw ?? "").Trim();
            if (id.Length == 0 || id.Length > WidgetSettings.MaxGaugeDisplayLength || id.Any(char.IsControl) ||
                kept.Contains(id, StringComparer.OrdinalIgnoreCase) || kept.Count >= MaxDisplays)
            {
                continue;
            }

            kept.Add(id);
        }

        changed = kept.Count != stored.Length || kept.Where((id, i) => !string.Equals(id, stored[i], StringComparison.Ordinal)).Any();
        return changed ? kept.ToArray() : stored;
    }

    // The displays the card goes on, in the order Windows lists them.
    //
    // Where the gauge is: the display the Gauge display setting names, as the gauge's own reader resolves it (the main display
    // for "" and for All displays, the main display too while the chosen one is not connected). All: every display. A set:
    // each chosen display that is connected; when none of them is, the card goes where the gauge is rather than nowhere, a
    // design choice so an open is never shown on no display just because the chosen ones are unplugged.
    public static IReadOnlyList<DisplayInfo> Targets(IReadOnlyList<string>? stored, string gaugeDisplaySetting, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            return Array.Empty<DisplayInfo>();
        }

        if (IsAll(stored))
        {
            return displays;
        }

        if (!IsWhereTheGaugeIs(stored))
        {
            var chosen = displays.Where(d => stored!.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            if (chosen.Count > 0)
            {
                return chosen;
            }
        }

        (DisplayInfo? gauge, _) = GaugeDisplayChoice.Resolve(gaugeDisplaySetting, displays);
        return gauge is null ? Array.Empty<DisplayInfo>() : new[] { gauge };
    }

    // The stored value after a display's box is ticked or cleared on the settings page. Ticking a box starts a set from the
    // displays the card is on now (so ticking a second display while it is where the gauge is keeps the gauge's display);
    // clearing the last box of a set goes back to where the gauge is.
    public static string[] WithDisplay(IReadOnlyList<string>? stored, string id, bool on, string gaugeDisplaySetting, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        List<string> set = Targets(stored, gaugeDisplaySetting, displays).Select(d => d.Id).ToList();
        set.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (on)
        {
            set.Add(id);
        }

        return set.Count == 0 ? [] : set.ToArray();
    }

    // What the Displays row's choice button says for the stored value.
    public static string Label(IReadOnlyList<string>? stored) =>
        IsAll(stored) ? WidgetCopy.CaseCardAllDisplays
        : IsWhereTheGaugeIs(stored) ? WidgetCopy.CaseCardWhereTheGaugeIs
        : WidgetCopy.CaseCardChosenDisplays;

    // The choice after the current one on the Displays row's button: where the gauge is, then all displays, then back. A set
    // steps to where the gauge is.
    public static string[] Next(IReadOnlyList<string>? stored) => IsWhereTheGaugeIs(stored) ? [All] : [];
}

// Whether a full-screen application is on one display, for the case-open card. The same rule the gauge hides by
// (FullScreenRule): SHQueryUserNotificationState is global, so it only says that a full-screen application or presentation
// settings exist; the foreground window says on which display. Presentation settings, the screen saver or a locked or
// switched session, quiet time and a failed read are no display in particular, so the card goes on none.
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state
internal static class CaseOpenCardFullScreen
{
    // The notification state lets the card go up on some display: accepting notifications, an app in its own
    // notification mode (as the tray's other unrequested cards allow), or a full-screen application, which hides it only
    // on that application's display.
    public static bool AllowsAnyDisplay(int state) =>
        CardPresenter.AcceptsUnrequestedCard(state) || state is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN;

    // A full-screen application covers this display: the state says one runs, and the foreground window covers this
    // display's bounds (with one display, the state alone, since it can only be that display; with several and a foreground
    // window that cannot be read, every display counts as covered, as the gauge does).
    public static bool Covers(int state, int displayCount, ForegroundWindowReading? foreground, DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!AllowsAnyDisplay(state))
        {
            return true;
        }

        return state is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN &&
            FullScreenRule.CoversGaugeDisplay(displayCount, foreground, display.Bounds);
    }
}

// Where the case-open card rests on one display: above the gauge when a gauge is shown on that display (the maths the
// gauge's own card uses), otherwise in that display's work area at the corner nearest its taskbar's end. Pure.
internal static class CaseOpenCardPlacement
{
    public static Rectangle On(DisplayInfo display, Size card, Rectangle? gauge, GaugePosition position)
    {
        ArgumentNullException.ThrowIfNull(display);
        int dpi = display.Dpi > 0 ? display.Dpi : CardPlacement.BaseDpi;
        if (gauge is { } g && display.Bounds.IntersectsWith(g))
        {
            return WidgetCardPlacement.Above(g, card, display.WorkArea, dpi, position);
        }

        Rectangle area = CardPlacement.Deflate(display.WorkArea, CardPlacement.Scale(WidgetCardPlacement.GapAt96, dpi));
        return CardPlacement.NearTray(card, EdgeOf(display), area);
    }

    // The taskbar's edge from where the work area stops short of the bounds; the bottom when it does not (an auto-hidden
    // or absent taskbar).
    internal static TaskbarEdge EdgeOf(DisplayInfo display)
    {
        Rectangle b = display.Bounds;
        Rectangle w = display.WorkArea;
        if (w.Top > b.Top)
        {
            return TaskbarEdge.Top;
        }

        if (w.Left > b.Left)
        {
            return TaskbarEdge.Left;
        }

        if (w.Right < b.Right)
        {
            return TaskbarEdge.Right;
        }

        return TaskbarEdge.Bottom;
    }
}

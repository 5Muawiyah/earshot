namespace Earshot.Widget;

// Which page the card shows. Main is the three-column card; Settings is the settings page; Update is the update
// page; History is the battery history page (a chart of one day, HistoryView). The update page is drawn from a SetupViewModel, since it is a status body and footer buttons on the
// sub-page frame.
internal enum WidgetCardView { Main, Settings, Update, History }

internal enum SetupIcon { None, Spinner, Check, Caution, Down, Shield }

// The update page's buttons (Update, Check, SetUp, Repair, Switch, and Cancel and TryAgain while it downloads or
// has failed), and Back, any sub-page's back button.
internal enum SetupAction { Cancel, TryAgain, Back, Update, Check, SetUp, Repair, Switch, ToggleAutoCheck, WhatsNew, HistoryEarlier, HistoryLater }

internal sealed record SetupButton(string Label, bool Primary, SetupAction Action);

// Everything one sub-page draws, handed in by WidgetCardPresenter. Immutable.
internal sealed record SetupViewModel(
    string Title,            // "Updates"
    string? Step,            // a step counter, or null for a page with none
    string? Prompt,          // the semibold line, or null
    string? Caption,         // the 12 px line under the prompt, or null
    SetupIcon Icon,
    string? Status,          // the line beside the icon
    string? StatusSub,
    IReadOnlyList<SetupButton> Buttons,
    int SpinnerFrame,        // 0..9, advanced by the presenter's animation timer
    bool ShowProgress = false,
    int? ProgressPercent = null) // null while the size is not known: the bar shows no fill and no figure
{
    public const int SpinnerFrames = 10;

    // The rows under the status on the updates page: Check automatically, What's new and, with an install, Repair. Null on
    // every other page.
    public UpdatesRows? Rows { get; init; }

    // The least height of the body, in epx at 100%, for a page whose content is supplied later (the history page's frame). A
    // design choice: tall enough to read as a page, not a strip.
    public int MinBodyAt96 { get; init; }

    // The battery history page's day, window and step buttons. Null on every other page.
    public HistoryView? History { get; init; }
}

// What the updates page's own rows need: whether Check automatically is on, whether an install exists (Repair is offered),
// and the running version for the status row's title.
internal sealed record UpdatesRows(bool AutoCheck, bool ShowRepair, string? Version);

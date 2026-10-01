namespace Earshot.Widget;

// Which page the card shows. Main is the three-column card; Settings is the settings page; Update is the update
// page. The update page is drawn from a SetupViewModel, since it is a status body and footer buttons on the
// sub-page frame.
internal enum WidgetCardView { Main, Settings, Update }

internal enum SetupIcon { None, Spinner, Check, Caution, Down, Shield }

// The update page's buttons (Update, Check, SetUp, Repair, Switch, and Cancel and TryAgain while it downloads or
// has failed), and Back, any sub-page's back button.
internal enum SetupAction { Cancel, TryAgain, Back, Update, Check, SetUp, Repair, Switch }

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
}

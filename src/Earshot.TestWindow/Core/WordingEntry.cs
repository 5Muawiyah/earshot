namespace Earshot.TestWindow.Core;

// Data\wording.json holds one entry per script string. Kind says which bound member the lookup is
// against: Question (Read-Answer, bound.Question), Note (Read-Note, bound.Question), Instruction
// (Wait-Owner, bound.Text), Consequence (Confirm-Step, bound.Consequence), Precondition
// (Show-Preconditions, one entry per element of bound.Preconditions), Action (Show-Preconditions,
// one entry per element of bound.PhysicalActions). Check is different in kind: it is never looked
// up against a script literal at all, only against a criterion id from result.json (ResultPresenter
// reads it, keyed on Test and ScriptText holding the criterion id, never scanned by
// WordingManifestTests' literal-match rule the way every other kind is, only by its
// Contains-in-script check, which a criterion id still passes because the id itself always appears
// literally inside its own -Id '...' declaration).
internal enum WordingKind
{
    Question,
    Note,
    Instruction,
    Consequence,
    Precondition,
    Action,
    Check,
}

// A button for a Read-Note question the window never lets the owner type into: the label shown,
// and the exact text recorded as if it had been typed.
internal sealed record WordingChoice(string Label, string Recorded);

// PlainMeaning is used only by a Check entry: an optional second line explaining what the check
// failing (or not settling) actually means for the owner, beyond its plain name alone. Null for
// every other kind. HowTo is the resolved list of named how-to blocks this entry asked for
// (wording.json's own "howTo" array of names, already looked up against "howToBlocks" at load
// time), empty for most entries: only Question, Instruction, Precondition and Action ever carry
// one in practice, since a Consequence and a Check describe what the code itself does, not
// something the owner has to go and do.
internal sealed record WordingEntry(
    string Test, WordingKind Kind, string ScriptText, string Plain, IReadOnlyList<WordingChoice> Choices, string? PlainMeaning = null)
{
    public IReadOnlyList<HowToBlock> HowTo { get; init; } = Array.Empty<HowToBlock>();
}

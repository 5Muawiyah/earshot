namespace Earshot.TestWindow.Core;

// Data\wording.json holds one entry per script string. Kind says which bound member the lookup is
// against: Question (Read-Answer, bound.Question), Note (Read-Note, bound.Question), Instruction
// (Wait-Owner, bound.Text), Consequence (Confirm-Step, bound.Consequence). Show-Preconditions has
// no entry kind: its own words are always shown verbatim, never looked up.
internal enum WordingKind
{
    Question,
    Note,
    Instruction,
    Consequence,
}

// A button for a Read-Note question the window never lets the owner type into: the label shown,
// and the exact text recorded as if it had been typed.
internal sealed record WordingChoice(string Label, string Recorded);

internal sealed record WordingEntry(string Test, WordingKind Kind, string ScriptText, string Plain, IReadOnlyList<WordingChoice> Choices);

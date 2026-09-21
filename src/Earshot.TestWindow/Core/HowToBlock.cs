namespace Earshot.TestWindow.Core;

// A reusable, named set of short physical actions (Data\wording.json's own "howToBlocks" object),
// with an optional picture: for someone who does not know where the Earshot icon is or what a full
// shut down means in practice, "click the icon" is not enough on its own. A wording entry (question,
// instruction, precondition or action) names zero or more of these by Name; PromptPresenter resolves
// the names into real HowToBlock values once, at load time, so a missing name is caught then rather
// than read as silently empty.
internal sealed record HowToBlock(string Name, IReadOnlyList<string> Steps, string? Picture);

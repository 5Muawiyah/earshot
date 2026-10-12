namespace Earshot.Widget;

// What is kept of the link to the owner's pair so that it can be found again after a silence or a restart: the model and
// colour of the pair that was linked when its case was opened by the PC, when that was, and when it was last heard.
// Nothing that names a sender is kept: no address, no per-run tag, no device name. A pair that sends from new addresses
// is told from a stranger's by the rule in RefindRule, not by anything stored here.
internal sealed record LinkMemory(ushort Model, byte Colour, DateTimeOffset LinkedAt, DateTimeOffset LastHeardAt);

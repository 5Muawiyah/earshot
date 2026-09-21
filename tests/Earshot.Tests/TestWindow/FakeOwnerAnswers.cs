namespace Earshot.Tests.TestWindow;

// Mirrors Fakes.psm1's own Get-FakeAnswer/Get-FakeNote exactly: first case-insensitive Contains
// match on the question text, in the table's own declaration order, wins. Reads FakeOwnerTables
// (Export-FakeOwnerTables.ps1's read of Fakes.psm1 itself) rather than a second, hand-kept table,
// so a real prompt answered through the window's own protocol gets the same answer the self-test's
// fakes would give it for the "one" case, which is what makes the produced evidence comparable to
// expectations.psd1 at all.
//
// Unlike Get-FakeAnswer/Get-FakeNote, an unmatched question throws rather than falling back to the
// first option: a shipped script asking something Fakes.psm1 has no answer for is a real gap this
// sweep must fail on, not paper over with a guess.
internal sealed class FakeOwnerAnswers
{
    private readonly FakeOwnerTables _tables;

    internal FakeOwnerAnswers(FakeOwnerTables tables) => _tables = tables;

    internal string Answer(string question, IReadOnlyList<string> options)
    {
        string lower = question.ToLowerInvariant();
        foreach ((string key, string value) in _tables.Answers)
        {
            if (!lower.Contains(key, StringComparison.Ordinal))
            {
                continue;
            }

            return options.Any(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase))
                ? value
                : throw new InvalidOperationException(
                    "Fakes.psm1's answer \"" + value + "\" is not one of the options [" + string.Join("/", options) + "] for: " + question);
        }

        throw new InvalidOperationException("No answer in Fakes.psm1 for the question: " + question);
    }

    internal string Note(string question)
    {
        string lower = question.ToLowerInvariant();
        foreach ((string key, string value) in _tables.Notes)
        {
            if (lower.Contains(key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new InvalidOperationException("No note in Fakes.psm1 for the question: " + question);
    }
}

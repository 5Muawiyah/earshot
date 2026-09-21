using System.Text.Json;

namespace Earshot.TestWindow.Core;

// Reads Data\wording.json and looks an entry up by exact match: by exact
// scriptText against the caller's own bound.Question, bound.Text or bound.Consequence, scoped to
// the row number so two tests sharing an exact sentence never cross-match.
internal static class Wording
{
    internal static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, "Data", "wording.json");

    internal static IReadOnlyList<WordingEntry> Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = new List<WordingEntry>();
        foreach (JsonElement item in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            string test = item.GetProperty("test").GetString()!;
            WordingKind kind = ParseKind(item.GetProperty("kind").GetString()!);
            string scriptText = item.GetProperty("scriptText").GetString()!;
            string plain = item.GetProperty("plain").GetString()!;

            var choices = new List<WordingChoice>();
            if (item.TryGetProperty("choices", out JsonElement choicesElement) && choicesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choicesElement.EnumerateArray())
                {
                    choices.Add(new WordingChoice(choice.GetProperty("label").GetString()!, choice.GetProperty("recorded").GetString()!));
                }
            }

            entries.Add(new WordingEntry(test, kind, scriptText, plain, choices));
        }

        return entries;
    }

    private static WordingKind ParseKind(string value) => value switch
    {
        "question" => WordingKind.Question,
        "note" => WordingKind.Note,
        "instruction" => WordingKind.Instruction,
        "consequence" => WordingKind.Consequence,
        _ => throw new FormatException("Unknown wording kind '" + value + "'."),
    };

    // No match: the script's own words are shown as the instruction. Never throws
    // and never guesses a plain line that was not written for this exact sentence.
    internal static WordingEntry? Find(IReadOnlyList<WordingEntry> entries, string test, WordingKind kind, string scriptText)
    {
        foreach (WordingEntry entry in entries)
        {
            if (entry.Test == test && entry.Kind == kind && string.Equals(entry.ScriptText, scriptText, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}

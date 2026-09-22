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
        JsonElement root = document.RootElement;

        Dictionary<string, HowToBlock> blocksByName = LoadHowToBlocks(root);

        var entries = new List<WordingEntry>();
        foreach (JsonElement item in root.GetProperty("entries").EnumerateArray())
        {
            string test = item.GetProperty("test").GetString()!;
            WordingKind kind = ParseKind(item.GetProperty("kind").GetString()!);
            string scriptText = item.GetProperty("scriptText").GetString()!;
            string plain = item.GetProperty("plain").GetString()!;
            string? plainMeaning = item.TryGetProperty("plainMeaning", out JsonElement meaningElement) ? meaningElement.GetString() : null;

            var choices = new List<WordingChoice>();
            if (item.TryGetProperty("choices", out JsonElement choicesElement) && choicesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choicesElement.EnumerateArray())
                {
                    choices.Add(new WordingChoice(choice.GetProperty("label").GetString()!, choice.GetProperty("recorded").GetString()!));
                }
            }

            var howTo = new List<HowToBlock>();
            if (item.TryGetProperty("howTo", out JsonElement howToElement) && howToElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement nameElement in howToElement.EnumerateArray())
                {
                    string name = nameElement.GetString()!;
                    if (!blocksByName.TryGetValue(name, out HowToBlock? block))
                    {
                        throw new FormatException(
                            "wording.json's entry for test '" + test + "' (" + kind + ", " + scriptText + ") names the how-to block '" +
                            name + "', which is not in howToBlocks.");
                    }

                    howTo.Add(block);
                }
            }

            entries.Add(new WordingEntry(test, kind, scriptText, plain, choices, plainMeaning) { HowTo = howTo });
        }

        return entries;
    }

    // "howToBlocks" is optional (older data files, or a test fixture with none of its own), so its
    // absence is never an error, only an empty lookup.
    private static Dictionary<string, HowToBlock> LoadHowToBlocks(JsonElement root)
    {
        var blocks = new Dictionary<string, HowToBlock>(StringComparer.Ordinal);
        if (!root.TryGetProperty("howToBlocks", out JsonElement blocksElement) || blocksElement.ValueKind != JsonValueKind.Object)
        {
            return blocks;
        }

        foreach (JsonProperty property in blocksElement.EnumerateObject())
        {
            var steps = new List<string>();
            if (property.Value.TryGetProperty("steps", out JsonElement stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement step in stepsElement.EnumerateArray())
                {
                    steps.Add(step.GetString()!);
                }
            }

            string? picture = property.Value.TryGetProperty("picture", out JsonElement pictureElement) && pictureElement.ValueKind == JsonValueKind.String
                ? pictureElement.GetString()
                : null;

            blocks[property.Name] = new HowToBlock(property.Name, steps, picture);
        }

        return blocks;
    }

    private static WordingKind ParseKind(string value) => value switch
    {
        "question" => WordingKind.Question,
        "note" => WordingKind.Note,
        "instruction" => WordingKind.Instruction,
        "consequence" => WordingKind.Consequence,
        "precondition" => WordingKind.Precondition,
        "action" => WordingKind.Action,
        "check" => WordingKind.Check,
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

    // A precondition or physical action line can be a compound script line: a fixed opening
    // phrase concatenated at runtime with dynamic, already-plain content (10-ShutdownMessages.ps1's
    // own per-variant restart instruction is the one case among the eighteen shipped scripts). Its
    // wording entry is keyed on the fixed opening phrase alone, because that is the only substring
    // guaranteed to appear literally in the script; whatever follows it is shown exactly as the
    // script produced it, since it is real, on-screen information (what to click or type), never
    // jargon needing a translation. Exact match is tried first, the same as Find; a prefix match is
    // tried only for Precondition and Action, and only once no exact match exists, so this can never
    // change what Question, Note, Instruction or Consequence resolve to. The longest matching
    // prefix wins, so a short, unrelated entry can never shadow a more specific one.
    internal static (string Plain, string TechnicalSuffix) FindPreconditionOrAction(
        IReadOnlyList<WordingEntry> entries, string test, WordingKind kind, string scriptText)
    {
        WordingEntry? exact = Find(entries, test, kind, scriptText);
        if (exact is not null)
        {
            return (exact.Plain, string.Empty);
        }

        WordingEntry? bestPrefix = null;
        foreach (WordingEntry entry in entries)
        {
            if (entry.Test == test && entry.Kind == kind && entry.ScriptText.Length > 0 &&
                scriptText.StartsWith(entry.ScriptText, StringComparison.Ordinal) &&
                (bestPrefix is null || entry.ScriptText.Length > bestPrefix.ScriptText.Length))
            {
                bestPrefix = entry;
            }
        }

        // No entry at all: the script's own words stand in for the plain line, the same fallback
        // every other kind uses, so a genuinely missing translation is still visible rather than
        // blank.
        return bestPrefix is null ? (scriptText, string.Empty) : (bestPrefix.Plain, scriptText.Substring(bestPrefix.ScriptText.Length));
    }
}

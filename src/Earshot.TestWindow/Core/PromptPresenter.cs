using System.Text.Json;
using System.Text.RegularExpressions;

namespace Earshot.TestWindow.Core;

// One button on screen: its label and the exact token sent as the reply. Wait-Owner's "No"
// (design.md section 7.1) sends nothing at all and just shows a message, so SendsReply is false
// for it; Reply is unused in that case.
internal sealed record PromptButton(string Label, string Reply, bool SendsReply = true);

// What StepPanel renders for one prompt message, decided here rather than in the form
// (design.md section 5: "Forms hold no decisions"). test-gui.md section 7.1's table, one branch
// per caller.
internal sealed record PresentedPrompt
{
    public required string Heading { get; init; }
    public required string PlainLine { get; init; }
    public string? DetailLabel { get; init; }
    public string? DetailText { get; init; }
    public IReadOnlyList<string> ListItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PhysicalActions { get; init; } = Array.Empty<string>();
    public required string ScriptOwnWords { get; init; }
    public required IReadOnlyList<PromptButton> Buttons { get; init; }
    public bool StopOnly { get; init; }
}

internal static class PromptPresenter
{
    private static readonly string[] KnownHelpers =
        { "Show-Preconditions", "Confirm-Step", "Read-Answer", "Read-Note", "Wait-Owner" };
    private static readonly PromptButton[] YesNoButtons = { new("Yes", "y"), new("No", "n") };
    private static readonly string[] DefaultYesNoUnsure = { "yes", "no", "unsure" };

    internal static PresentedPrompt Present(
        ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording, IReadOnlyList<string>? transcriptSoFar = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Kind != ChildMessageKind.Prompt)
        {
            throw new ArgumentException("Not a prompt message.", nameof(message));
        }

        // Options come from bound.Options when present, else from the prompt text [a/b/c]
        // (section 7.2). Read-Answer is the only caller that ever carries either.
        IReadOnlyList<string>? boundOptions = ReadOptionsFromBound(message);
        IReadOnlyList<string>? textOptions = ReadOptionsFromPromptText(message.Prompt);
        bool optionsDisagree = boundOptions is not null && textOptions is not null &&
            !boundOptions.SequenceEqual(textOptions, StringComparer.Ordinal);

        if (!KnownHelpers.Contains(message.Caller) || optionsDisagree)
        {
            return new PresentedPrompt
            {
                Heading = "This test asked something the window does not recognise",
                PlainLine = message.Prompt ?? string.Empty,
                ScriptOwnWords = message.Prompt ?? string.Empty,
                Buttons = Array.Empty<PromptButton>(),
                StopOnly = true,
            };
        }

        return message.Caller switch
        {
            "Show-Preconditions" => PresentPreconditions(message),
            "Confirm-Step" => PresentConfirmStep(message, testNumber, wording),
            "Read-Answer" => PresentReadAnswer(message, testNumber, wording, boundOptions ?? textOptions ?? DefaultYesNoUnsure),
            "Read-Note" => PresentReadNote(message, testNumber, wording, transcriptSoFar ?? Array.Empty<string>()),
            "Wait-Owner" => PresentWaitOwner(message, testNumber, wording),
            _ => throw new InvalidOperationException("KnownHelpers and this switch have drifted apart: " + message.Caller),
        };
    }

    private static PresentedPrompt PresentPreconditions(ChildMessage message)
    {
        var preconditions = new List<string>();
        if (message.Bound.TryGetValue("Preconditions", out string? raw))
        {
            preconditions.AddRange(ReadStringArray(raw));
        }

        var physicalActions = new List<string>();
        if (message.Bound.TryGetValue("PhysicalActions", out string? rawActions))
        {
            physicalActions.AddRange(ReadStringArray(rawActions));
        }

        return new PresentedPrompt
        {
            Heading = "Before this test starts",
            PlainLine = "Are all of those true, and are you ready to start?",
            ListItems = preconditions,
            PhysicalActions = physicalActions,
            ScriptOwnWords = message.Prompt ?? string.Empty,
            Buttons = YesNoButtons,
        };
    }

    private static PresentedPrompt PresentConfirmStep(ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording)
    {
        string consequence = message.Bound.GetValueOrDefault("Consequence", string.Empty);
        WordingEntry? entry = Wording.Find(wording, testNumber, WordingKind.Consequence, consequence);
        string heading = "Run this step now?";
        if (message.Stack.Contains("Close-AtRest", StringComparer.Ordinal))
        {
            heading = "Put this PC back at rest?";
        }

        string plain = entry?.Plain ?? consequence;
        return new PresentedPrompt
        {
            Heading = heading,
            PlainLine = plain,
            DetailLabel = "What it does:",
            DetailText = consequence,
            ScriptOwnWords = consequence,
            Buttons = YesNoButtons,
        };
    }

    private static PresentedPrompt PresentReadAnswer(
        ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording, IReadOnlyList<string> options)
    {
        string question = message.Bound.GetValueOrDefault("Question", string.Empty);
        WordingEntry? entry = Wording.Find(wording, testNumber, WordingKind.Question, question);
        string plain = entry?.Plain ?? question;

        var buttons = new List<PromptButton>();
        foreach (string option in options)
        {
            string label = OptionButtonLabel(option, question);
            buttons.Add(new PromptButton(label, option));
        }

        return new PresentedPrompt
        {
            Heading = "Question",
            PlainLine = plain,
            ScriptOwnWords = question,
            Buttons = buttons,
        };
    }

    // Section 7.3's fixed relabelling for the two tests whose Read-Answer offers on/off/leave
    // rather than yes/no/unsure. Every other option keeps its own word as its label (Yes, No,
    // Not sure are handled by the caller through the ordinary yes/no/unsure path below).
    private static string OptionButtonLabel(string option, string question) => option switch
    {
        "yes" => "Yes",
        "no" => "No",
        "unsure" => "Not sure",
        "on" when question.Contains("Leave protection on", StringComparison.Ordinal) => "Leave call protection on, as Earshot ships",
        "off" when question.Contains("Leave protection on", StringComparison.Ordinal) => "Turn it off",
        "on" => "On, as Earshot ships",
        "off" => "Off",
        "leave" => "Leave it as it is",
        _ => option,
    };

    // The line after "Addresses seen in the Bluetooth node list, other than the pinned one:" in
    // 14-SetDeviceRefusal.ps1: each address is printed on its own line, "  " plus twelve upper
    // case hexadecimal characters, nothing else.
    private static readonly Regex AddressLine = new(@"^\s*([0-9A-F]{12})\s*$", RegexOptions.Compiled);
    private const string AddressesSeenMarker = "Addresses seen";

    private static PresentedPrompt PresentReadNote(
        ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording, IReadOnlyList<string> transcriptSoFar)
    {
        string question = message.Bound.GetValueOrDefault("Question", string.Empty);
        WordingEntry? entry = Wording.Find(wording, testNumber, WordingKind.Note, question);
        string plain = entry?.Plain ?? question;

        var buttons = new List<PromptButton>();
        if (entry is not null && entry.Choices.Count > 0)
        {
            foreach (WordingChoice choice in entry.Choices)
            {
                buttons.Add(new PromptButton(choice.Label, choice.Recorded));
            }
        }
        else
        {
            // section 7.3, test 14: no static choices are written for this one because the
            // addresses are printed at run time. One button per address the script has just
            // printed, sending that address, and "None of these is my phone" (empty, which the
            // script records as inconclusive). No address lines parsed: only the last button.
            foreach (string address in AddressesFromTranscript(transcriptSoFar))
            {
                buttons.Add(new PromptButton(address, address));
            }

            buttons.Add(new PromptButton("None of these is my phone", string.Empty));
        }

        return new PresentedPrompt
        {
            Heading = "Note",
            PlainLine = plain,
            ScriptOwnWords = question,
            Buttons = buttons,
        };
    }

    private static List<string> AddressesFromTranscript(IReadOnlyList<string> transcriptSoFar)
    {
        var addresses = new List<string>();
        bool afterMarker = false;
        foreach (string line in transcriptSoFar)
        {
            if (!afterMarker)
            {
                if (line.Contains(AddressesSeenMarker, StringComparison.Ordinal))
                {
                    afterMarker = true;
                }

                continue;
            }

            Match match = AddressLine.Match(line);
            if (match.Success)
            {
                addresses.Add(match.Groups[1].Value);
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                // The first non-address, non-blank line after the marker (the "Windows
                // Bluetooth settings shows..." line) ends the list.
                break;
            }
        }

        return addresses;
    }

    private static PresentedPrompt PresentWaitOwner(ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording)
    {
        string text = message.Bound.GetValueOrDefault("Text", string.Empty);
        WordingEntry? entry = Wording.Find(wording, testNumber, WordingKind.Instruction, text);
        string plain = entry?.Plain ?? text;

        return new PresentedPrompt
        {
            Heading = "Do this:",
            PlainLine = plain,
            DetailLabel = "Have you done it?",
            ScriptOwnWords = text,
            Buttons = new[] { new PromptButton("Yes", string.Empty), new PromptButton("No", string.Empty, SendsReply: false) },
        };
    }

    private static List<string>? ReadOptionsFromBound(ChildMessage message)
    {
        if (!message.Bound.TryGetValue("Options", out string? raw))
        {
            return null;
        }

        return ReadStringArray(raw);
    }

    private static List<string> ReadStringArray(string raw)
    {
        var items = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        items.Add(element.GetString()!);
                    }
                }

                return items;
            }
        }
        catch (JsonException)
        {
            // Not a JSON array (a single bound value serialises as a bare string): fall through
            // and treat the raw text as the one item.
        }

        if (!string.IsNullOrEmpty(raw))
        {
            items.Add(raw);
        }

        return items;
    }

    // Read-Answer's own prompt text is always "  [a/b/c]" (LiveTest.psm1's Read-Answer).
    private static string[]? ReadOptionsFromPromptText(string? prompt)
    {
        if (prompt is null)
        {
            return null;
        }

        Match match = Regex.Match(prompt, @"\[([a-z/]+)\]");
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value.Split('/');
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Earshot.TestWindow.Core;

// One button on screen: its label and the exact token sent as the reply. Wait-Owner's "No" sends
// nothing at all and just shows a message, so SendsReply is false for it; Reply is unused in
// that case.
internal sealed record PromptButton(string Label, string Reply, bool SendsReply = true);

// What StepPanel renders for one prompt message, decided here rather than in the form: forms
// hold no decisions. One branch per caller.
internal sealed record PresentedPrompt
{
    public required string Heading { get; init; }
    public required string PlainLine { get; init; }
    public string? DetailLabel { get; init; }
    public string? DetailText { get; init; }

    // True only for Confirm-Step's own "What it does:" detail: the script's raw consequence text,
    // which belongs behind the technical-details toggle. Wait-Owner's "Have you done it?" detail
    // is this window's own plain wording, never the script's, so it stays false and is never hidden.
    public bool DetailIsTechnical { get; init; }

    // The script's own raw preconditions and physical actions, verbatim: shown only behind the
    // technical-details toggle.
    public IReadOnlyList<string> ListItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PhysicalActions { get; init; } = Array.Empty<string>();

    // The same two lists, translated through wording.json's precondition and action entries (or the
    // script's own words, unhidden, when no entry exists yet): always shown, on or off.
    public IReadOnlyList<string> PlainListItems { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PlainPhysicalActions { get; init; } = Array.Empty<string>();

    public required string ScriptOwnWords { get; init; }
    public required IReadOnlyList<PromptButton> Buttons { get; init; }
    public bool StopOnly { get; init; }

    // Zero or more named how-to blocks (wording.json's own "howToBlocks"), resolved and in order:
    // shown as a numbered list with a picture beside it, always visible (never behind the
    // technical-details toggle), since these are this window's own plain steps, never the script's
    // own words.
    public IReadOnlyList<HowToBlock> HowToBlocks { get; init; } = Array.Empty<HowToBlock>();
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

        // Options come from bound.Options when present, else from the prompt text [a/b/c].
        // Read-Answer is the only caller that ever carries either.
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
            "Show-Preconditions" => PresentPreconditions(message, testNumber, wording),
            "Confirm-Step" => PresentConfirmStep(message, testNumber, wording),
            "Read-Answer" => PresentReadAnswer(message, testNumber, wording, boundOptions ?? textOptions ?? DefaultYesNoUnsure),
            "Read-Note" => PresentReadNote(message, testNumber, wording, transcriptSoFar ?? Array.Empty<string>()),
            "Wait-Owner" => PresentWaitOwner(message, testNumber, wording),
            _ => throw new InvalidOperationException("KnownHelpers and this switch have drifted apart: " + message.Caller),
        };
    }

    private static PresentedPrompt PresentPreconditions(ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording)
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

        var howTo = new List<HowToBlock>();
        foreach (string p in preconditions)
        {
            howTo.AddRange(HowToFor(wording, testNumber, WordingKind.Precondition, p));
        }

        foreach (string a in physicalActions)
        {
            howTo.AddRange(HowToFor(wording, testNumber, WordingKind.Action, a));
        }

        return new PresentedPrompt
        {
            Heading = "Before this test starts",
            PlainLine = "Are all of those true, and are you ready to start?",
            ListItems = preconditions,
            PhysicalActions = physicalActions,
            PlainListItems = preconditions.Select(p => PlainPreconditionOrAction(wording, testNumber, WordingKind.Precondition, p)).ToList(),
            PlainPhysicalActions = physicalActions.Select(a => PlainPreconditionOrAction(wording, testNumber, WordingKind.Action, a)).ToList(),
            ScriptOwnWords = message.Prompt ?? string.Empty,
            Buttons = YesNoButtons,
            HowToBlocks = howTo,
        };
    }

    private static string PlainPreconditionOrAction(IReadOnlyList<WordingEntry> wording, string testNumber, WordingKind kind, string scriptText)
    {
        (string plain, string technicalSuffix) = Wording.FindPreconditionOrAction(wording, testNumber, kind, scriptText);
        return plain + technicalSuffix;
    }

    // The exact-match entry's own how-to blocks; the prefix-fallback entry used for a compound
    // line (10-ShutdownMessages.ps1's own restart action) carries its blocks the same way, since
    // Find (called first inside FindPreconditionOrAction) already covers the exact-match case and
    // a direct lookup here is simpler than threading the tuple through.
    private static IReadOnlyList<HowToBlock> HowToFor(IReadOnlyList<WordingEntry> wording, string testNumber, WordingKind kind, string scriptText)
    {
        WordingEntry? exact = Wording.Find(wording, testNumber, kind, scriptText);
        if (exact is not null)
        {
            return exact.HowTo;
        }

        foreach (WordingEntry entry in wording)
        {
            if (entry.Test == testNumber && entry.Kind == kind && entry.ScriptText.Length > 0 &&
                scriptText.StartsWith(entry.ScriptText, StringComparison.Ordinal))
            {
                return entry.HowTo;
            }
        }

        return Array.Empty<HowToBlock>();
    }

    // LiveTest.psm1's own shared default for every Close-AtRest step that does not pass its own
    // -Consequence (00-Restore.ps1 and 02-Disconnect.ps1 do; every other shipped script relies on
    // this one): a module-level PowerShell variable, never a literal any single script repeats, so
    // it can never carry a wording.json entry under this project's own "scriptText must be in its
    // own script" rule (WordingManifestTests.EveryWordingEntrysScriptTextIsInItsScript). Matched
    // exactly, so 00 and 02's own distinct consequence text is never swapped for this one.
    private const string CloseAtRestSharedConsequence =
        "Blocks the AirPods Bluetooth nodes so this PC does not page them at the next boot. " +
        "If the AirPods are playing through this PC right now, that stops.";

    private const string CloseAtRestSharedConsequencePlain =
        "Stops this computer grabbing your AirPods when it starts up. If the AirPods are playing " +
        "through this computer right now, that stops too.";

    // The closing step's own disconnect-first offer (closing-step-disconnect-first.md D1/D2):
    // sent, through LiveTest.psm1's own $script:AtRestDisconnectConsequence, whenever render
    // reads ACTIVE or cannot be read, before the block offer. Same reasoning as the shared default
    // above: a module-level PowerShell variable, never a literal any single script repeats, so it
    // can never carry a wording.json entry either. Matched exactly.
    private const string CloseAtRestDisconnectConsequence =
        "Disconnects the AirPods from this PC first, with the same one-shot disconnect a left click sends, " +
        "and reads the render endpoint again. Windows refuses to disable the A2DP sink entry while it is rendering " +
        "(CR_REMOVE_VETOED, 21 September 2026), so a block sent now would only partly take. Your AirPods will stop playing from this computer.";

    private const string CloseAtRestDisconnectConsequencePlain =
        "Your AirPods will stop playing from this computer, so that the next step can block them.";

    // What the block offer says when the disconnect above was declined, did not confirm, failed to
    // start or timed out: LiveTest.psm1's own $script:AtRestBlockWhilePlayingConsequence.
    private const string CloseAtRestBlockWhilePlayingConsequence =
        "Blocks the AirPods Bluetooth nodes so this PC does not page them at the next boot. " +
        "If the AirPods are playing through this PC right now, that stops. The AirPods still read as playing from this PC, " +
        "so Windows may refuse the audio entry as it did on 21 September; the re-read afterwards decides.";

    private const string CloseAtRestBlockWhilePlayingConsequencePlain =
        "This computer will try to block your AirPods now. Because they may still be playing here, Windows may only " +
        "let part of it happen; the record will say.";

    private static PresentedPrompt PresentConfirmStep(ChildMessage message, string testNumber, IReadOnlyList<WordingEntry> wording)
    {
        string consequence = message.Bound.GetValueOrDefault("Consequence", string.Empty);
        WordingEntry? entry = Wording.Find(wording, testNumber, WordingKind.Consequence, consequence);
        string heading = "Run this step now?";
        if (message.Stack.Contains("Close-AtRest", StringComparer.Ordinal))
        {
            heading = "Stop this computer grabbing your AirPods again?";
        }

        // The disconnect offer gets its own heading: it is a different device action from the
        // block (it stops audio the owner can hear now), matched exactly the same way as the
        // shared and while-playing consequences below.
        if (consequence == CloseAtRestDisconnectConsequence)
        {
            heading = "Stop your AirPods playing from this computer?";
        }

        string plain = entry?.Plain
            ?? (consequence == CloseAtRestSharedConsequence ? CloseAtRestSharedConsequencePlain
                : consequence == CloseAtRestDisconnectConsequence ? CloseAtRestDisconnectConsequencePlain
                : consequence == CloseAtRestBlockWhilePlayingConsequence ? CloseAtRestBlockWhilePlayingConsequencePlain
                : consequence);
        return new PresentedPrompt
        {
            Heading = heading,
            PlainLine = plain,
            DetailLabel = "What it does:",
            DetailText = consequence,
            DetailIsTechnical = true,
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
            HowToBlocks = entry?.HowTo ?? Array.Empty<HowToBlock>(),
        };
    }

    // The fixed relabelling for the two tests whose Read-Answer offers on/off/leave
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
            // Test 14: no static choices are written for this one because the
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
            HowToBlocks = entry?.HowTo ?? Array.Empty<HowToBlock>(),
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

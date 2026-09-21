using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Earshot.TestWindow.Core;

internal enum ChildMessageKind
{
    Hello,
    Prompt,
    Exit,
    Crash,

    // A line carrying the prefix that this window could not read as one of the four message
    // types above: malformed base64, invalid JSON, or a "type" this protocol does not know.
    // Never guessed at, always shown as what it is.
    Unreadable,
}

// One line the child sent, decoded. Nothing here is ever a live step; it is one message from the
// wire this protocol describes.
internal sealed class ChildMessage
{
    public required ChildMessageKind Kind { get; init; }

    // hello
    public int? ProtocolVersion { get; init; }
    public int? ChildProcessId { get; init; }
    public string? PsVersion { get; init; }
    public string? Script { get; init; }
    public string? Half { get; init; }

    // prompt
    public int Seq { get; init; }
    public string? Caller { get; init; }
    public IReadOnlyList<string> Stack { get; init; } = Array.Empty<string>();
    public string? Prompt { get; init; }
    public IReadOnlyDictionary<string, string> Bound { get; init; } = new Dictionary<string, string>();

    // exit
    public int? ExitCode { get; init; }

    // crash / unreadable
    public string? Text { get; init; }
}

// The message and reply grammar. Every stdout line either carries the prefix and
// decodes to one of hello/prompt/exit/crash, or is transcript. A line with the prefix that this
// cannot decode is Unreadable, never dropped and never guessed at.
internal static class Protocol
{
    internal const string Prefix = "@@EARSHOT-TW@@ ";

    internal static bool IsProtocolLine(string line) => line.StartsWith(Prefix, StringComparison.Ordinal);

    internal static ChildMessage ParseMessage(string line)
    {
        if (!IsProtocolLine(line))
        {
            throw new ArgumentException("Not a protocol line: " + line, nameof(line));
        }

        string wire = line[Prefix.Length..];
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(wire);
        }
        catch (FormatException ex)
        {
            return Unreadable("The line after the prefix was not valid base64: " + ex.Message + " (" + line + ")");
        }

        string json;
        try
        {
            json = Encoding.UTF8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            return Unreadable("The decoded bytes were not valid UTF-8: " + ex.Message);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Unreadable("The decoded text was not valid JSON: " + ex.Message + " (" + json + ")");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return Unreadable("The message had no string 'type' member: " + json);
            }

            string type = typeElement.GetString()!;
            return type switch
            {
                "hello" => ParseHello(root),
                "prompt" => ParsePrompt(root),
                "exit" => ParseExit(root),
                "crash" => ParseCrash(root),
                _ => Unreadable("Unknown message type '" + type + "': " + json),
            };
        }
    }

    private static ChildMessage ParseHello(JsonElement root) => new()
    {
        Kind = ChildMessageKind.Hello,
        ProtocolVersion = TryInt(root, "protocol"),
        ChildProcessId = TryInt(root, "pid"),
        PsVersion = TryString(root, "psVersion"),
        Script = TryString(root, "script"),
        Half = TryString(root, "half"),
    };

    private static ChildMessage ParsePrompt(JsonElement root)
    {
        int seq = TryInt(root, "seq") ?? -1;
        var stack = new List<string>();
        if (root.TryGetProperty("stack", out JsonElement stackElement) && stackElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in stackElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    stack.Add(item.GetString()!);
                }
            }
        }

        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("bound", out JsonElement boundElement) && boundElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in boundElement.EnumerateObject())
            {
                bound[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    _ => property.Value.GetRawText(),
                };
            }
        }

        return new ChildMessage
        {
            Kind = ChildMessageKind.Prompt,
            Seq = seq,
            Caller = TryString(root, "caller"),
            Stack = stack,
            Prompt = TryString(root, "prompt"),
            Bound = bound,
        };
    }

    private static ChildMessage ParseExit(JsonElement root) => new()
    {
        Kind = ChildMessageKind.Exit,
        ExitCode = TryInt(root, "code"),
    };

    private static ChildMessage ParseCrash(JsonElement root) => new()
    {
        Kind = ChildMessageKind.Crash,
        Text = TryString(root, "message"),
    };

    private static ChildMessage Unreadable(string text) => new() { Kind = ChildMessageKind.Unreadable, Text = text };

    private static string? TryString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? TryInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : null;

    // Window to child: "R <seq> <base64>", ASCII, newline terminated (StreamWriter.WriteLine adds
    // the newline). The wire stays pure ASCII: base64 of UTF-8 bytes is always ASCII.
    internal static string FormatReply(int seq, string reply)
    {
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(reply));
        return "R " + seq.ToString(CultureInfo.InvariantCulture) + " " + base64;
    }

    internal static string FormatAbort(int seq) => "A " + seq.ToString(CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Text.Json;

namespace Earshot.Setup;

internal sealed record AirPodsCandidate(string Name, string Address, Guid ContainerId);

// What the extracted program's "probe setup-values" says, read from the file it was told to write. The program ran from the
// staging folder, so the file is what that exact copy found: who is signed in, which paired AirPods there are, and what is
// installed now. Nothing in it is trusted as a command: the address and container are checked for their shape again here and
// again by install-zip, and the SID by both.
internal sealed record SetupValuesReading(
    string UserSid, bool Ready, string Reason, string Address, Guid ContainerId,
    IReadOnlyList<AirPodsCandidate> Candidates, string InstallState, string InstalledVersion)
{
    public const string StateNothing = "nothing";
    public const string StateUsable = "usable";
    public const string StateUnusable = "unusable";

    public static SetupValuesReading? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string sid = Text(root, "userSid");
            string address = Text(root, "address");
            _ = Guid.TryParse(Text(root, "containerId"), out Guid container);
            bool ready = root.TryGetProperty("ready", out JsonElement r) && r.ValueKind == JsonValueKind.True;
            var candidates = new List<AirPodsCandidate>();
            if (root.TryGetProperty("candidates", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string candidateAddress = Text(item, "address");
                    if (IsAddress(candidateAddress) && Guid.TryParse(Text(item, "containerId"), out Guid candidateContainer) && candidateContainer != Guid.Empty)
                    {
                        candidates.Add(new AirPodsCandidate(Text(item, "name"), candidateAddress, candidateContainer));
                    }
                }
            }

            string state = StateNothing;
            string version = "";
            if (root.TryGetProperty("install", out JsonElement install) && install.ValueKind == JsonValueKind.Object)
            {
                state = Text(install, "state");
                version = Text(install, "version");
            }

            return new SetupValuesReading(sid, ready && IsAddress(address) && container != Guid.Empty, Text(root, "reason"), address, container, candidates, state, version);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The SID of a signed-in account, digits and hyphens only, so it cannot carry anything into a command line.
    public static bool IsUserSid(string? sid) =>
        sid is { Length: > 8 and <= 80 } && sid.StartsWith("S-1-", StringComparison.Ordinal) && sid.All(c => char.IsAsciiDigit(c) || c == '-' || c == 'S');

    public static bool IsAddress(string? address) => address is { Length: 12 } && address.All(char.IsAsciiHexDigit);

    // The AirPods to install for: the one setup-values already chose (an install it knows, or exactly one pair), else every
    // candidate it listed. Empty when there are none.
    public IReadOnlyList<AirPodsCandidate> Choices()
    {
        if (Ready)
        {
            string name = Candidates.FirstOrDefault(c => string.Equals(c.Address, Address, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            return [new AirPodsCandidate(name, Address, ContainerId)];
        }

        return Candidates;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public static string Hex(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

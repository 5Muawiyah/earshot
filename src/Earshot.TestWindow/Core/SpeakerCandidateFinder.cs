using System.Text.Json;
using System.Text.RegularExpressions;

namespace Earshot.TestWindow.Core;

// One second-device candidate for test 14's own choice buttons: the bare address
// 14-SetDeviceRefusal.ps1 needs as -SpeakerAddress, plus the device name and the run the evidence
// came from, shown to the owner because a button offering only an address gives no way to tell
// two candidates apart, or to judge how stale the evidence choosing from is.
internal sealed record SpeakerCandidate(string Address, string? DeviceName, string SourceStamp, string SourceTestId)
{
    // The device name is shown only when the evidence actually held one (Get-NodeState's own
    // "name" field can be empty); the source run is always shown, since it is always known,
    // whatever the evidence itself does or does not carry.
    internal string DisplayText =>
        Address + (string.IsNullOrEmpty(DeviceName) ? string.Empty : " - " + DeviceName) + " (from " + SourceTestId + " " + SourceStamp + ")";
}

// 14-SetDeviceRefusal.ps1's own "Moving the pin off a protected device" half can only try what it
// is given as -SpeakerAddress, and the window has nowhere to type one in (no text box anywhere in
// this project, by design). This reads a second device's address instead, out of the same shape of
// file any earlier run's own node probe already wrote straight into its run folder
// (tools\live-tests\LiveTest.psm1's Get-NodeState writes "<label>.json", e.g. "nodes-before.json",
// beside that run's own result.json): a "nodes" array of objects each carrying an "instanceId" a
// twelve hex character address sits inside, an optional "name" (ProbeNodes.cs's own field), and
// one "address" member naming the device pinned now, which is excluded, the same shape
// 14-SetDeviceRefusal.ps1's own Get-OtherAddresses parses.
//
// Nothing here starts a process or opens a device: it only reads files a past run already left on
// disk, newest run first, and the first file that answers with at least one candidate wins: newest
// readable node evidence only, never merged across several files or runs.
internal static class SpeakerCandidateFinder
{
    // A twelve hex character run not immediately after a hyphen: a Bluetooth service GUID's own
    // final group (e.g. "-0000-1000-8000-00805F9B34FB}", the base UUID every classic profile
    // shares) is hyphen-delimited and exactly twelve hex characters long too, so without this the
    // pattern below would offer that fixed, meaningless GUID tail as though it were a paired
    // device, on every single node. A real address instead sits after "DEV_", "&" or "_" in the
    // instance id, never after a hyphen.
    private static readonly Regex AddressPattern = new(@"(?<!-)[0-9A-Fa-f]{12}", RegexOptions.Compiled);

    internal static IReadOnlyList<SpeakerCandidate> Find(string liveTestRoot)
    {
        if (!Directory.Exists(liveTestRoot))
        {
            return Array.Empty<SpeakerCandidate>();
        }

        foreach (string stampFolder in EnumerateStampFoldersNewestFirst(liveTestRoot))
        {
            string stamp = Path.GetFileName(stampFolder);
            foreach (string testFolder in Directory.EnumerateDirectories(stampFolder))
            {
                string testId = Path.GetFileName(testFolder);
                foreach (string file in Directory.EnumerateFiles(testFolder, "*.json"))
                {
                    IReadOnlyList<SpeakerCandidate> found = TryReadCandidates(file, stamp, testId);
                    if (found.Count > 0)
                    {
                        return found;
                    }
                }
            }
        }

        return Array.Empty<SpeakerCandidate>();
    }

    private static List<string> EnumerateStampFoldersNewestFirst(string liveTestRoot)
    {
        var stamps = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(liveTestRoot))
        {
            string name = Path.GetFileName(folder);
            if (EvidenceStore.IsRunStamp(name))
            {
                stamps.Add(folder);
            }
        }

        stamps.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
        return stamps;
    }

    // A file that is not this shape at all (result.json, an audio or task report, anything the
    // JSON parser rejects, anything that could not be read) is not a failure to report, only a
    // file with nothing to offer here; the caller moves on to the next one.
    private static IReadOnlyList<SpeakerCandidate> TryReadCandidates(string path, string stamp, string testId)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return Array.Empty<SpeakerCandidate>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<SpeakerCandidate>();
        }

        if (bytes.Length == 0)
        {
            return Array.Empty<SpeakerCandidate>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return Array.Empty<SpeakerCandidate>();
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("nodes", out JsonElement nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<SpeakerCandidate>();
            }

            string? pinned = root.TryGetProperty("address", out JsonElement addressElement) && addressElement.ValueKind == JsonValueKind.String
                ? addressElement.GetString()?.ToUpperInvariant()
                : null;

            var found = new List<SpeakerCandidate>();
            foreach (JsonElement node in nodesElement.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object ||
                    !node.TryGetProperty("instanceId", out JsonElement instanceElement) || instanceElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? name = node.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

                foreach (Match match in AddressPattern.Matches(instanceElement.GetString() ?? string.Empty))
                {
                    string candidate = match.Value.ToUpperInvariant();
                    if (string.Equals(candidate, pinned, StringComparison.Ordinal) || found.Any(c => c.Address == candidate))
                    {
                        continue;
                    }

                    found.Add(new SpeakerCandidate(candidate, name, stamp, testId));
                }
            }

            return found;
        }
    }
}

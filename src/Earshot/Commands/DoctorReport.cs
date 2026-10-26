using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Earshot.Diagnostics;

namespace Earshot.Commands;

// The AirPods models the report names, by the product id Windows holds in a Bluetooth node's instance id
// (_VID&0001004C_PID&xxxx: source 0001 is the Bluetooth SIG's list, 004C is Apple's vendor id there). 200E is the one model the
// furiousMAC Continuity notes name (github.com/furiousMAC/continuity, messages/proximity_pairing.md); 2027 is the id the owner's
// own pair reports, and no published source names it, so the report gives only the code. Any other Apple node is only counted,
// never named, because an iPhone has the same vendor id.
internal static partial class AirPodsModels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["200E"] = "AirPods Pro",
        ["2027"] = "model 2027",
    };

    [GeneratedRegex(@"_VID&0001004C_PID&(?<pid>[0-9A-Fa-f]{4})(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex ProductId();

    // The model name of an Apple node, "" for an Apple node of another model, null for a node that is not Apple's at all.
    public static string? ModelOf(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        if (!instanceId.Contains("_VID&0001004C_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match match = ProductId().Match(instanceId);
        return match.Success && Names.TryGetValue(match.Groups["pid"].Value, out string? name) ? name : "";
    }
}

// The doctor report: built from facts, safe to paste into a public issue. The keys and their order are the same as the install
// script's Doctor action prints; a test holds the two together.
internal static class DoctorReport
{
    internal const string HistoryPath = @"%LOCALAPPDATA%\Earshot\logs\connect-history.jsonl";

    // What stands for the folders of a path outside the profile and Program Files.
    internal const string OtherFolder = @"(another folder)\";

    private static readonly Environment.SpecialFolder[] ProgramFolders = [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86];

    private static bool InProgramFiles(string path)
    {
        foreach (Environment.SpecialFolder known in ProgramFolders)
        {
            string root = Environment.GetFolderPath(known);
            if (root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Windows' own Bluetooth enumerator is a lower filter on the RFCOMM node of a stock PC (read on a PC without any other
    // Bluetooth program), so it is not a filter from another program and the report leaves it out.
    private static readonly string[] InboxFilters = ["BthEnum"];

    private static readonly string[] HistoryKeys = ["time", "step", "outcome", "code", "attempt", "durationMs", "reason"];

    // The keys of a history line that hold a number, and the range each may hold. A time is Unix seconds up to the year 2100; a
    // code is a Win32 error, an HRESULT (signed or not) or an exit code; a try count and a duration in milliseconds (up to a day)
    // are small. A 48-bit address is far outside every one of them, as a number of any size.
    private static readonly Dictionary<string, (long Min, long Max)> NumericKeys = new(StringComparer.Ordinal)
    {
        ["time"] = (0, 4_102_444_800),
        ["code"] = (int.MinValue, uint.MaxValue),
        ["attempt"] = (0, 1_000),
        ["durationMs"] = (0, 86_400_000),
    };

    // A time written as text must be a plain UTC time and nothing else.
    private static readonly Regex IsoTime = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$", RegexOptions.CultureInvariant);

    private static readonly Regex ApprovedByte = new("^[0-9A-Fa-f]{2}$", RegexOptions.CultureInvariant);

    private static readonly Regex HexNumber = new("^0x[0-9A-Fa-f]{1,8}$", RegexOptions.CultureInvariant);

    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    private static readonly Regex OneWord = new("^[A-Za-z0-9_.:-]{1,40}$", RegexOptions.CultureInvariant);

    public static string Build(DoctorFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            Write(w, f);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter w, DoctorFacts f)
    {
        string user = f.Identity?.User ?? "";
        string computer = f.Identity?.Computer ?? "";
        var known = new List<string>();
        foreach (DeviceFact d in f.Devices ?? [])
        {
            foreach (string? v in new[] { d.Name, d.Serial, d.Container })
            {
                if (!string.IsNullOrEmpty(v))
                {
                    known.Add(v);
                }
            }
        }

        known.Add(computer);
        string Safe(string? text) => DiagnosticsRedactor.Redact(text, known, user);

        // A path with the profile or the local app data folder in it is written with the variable in its place, and one in Program
        // Files as it is. Any other path keeps only its last part: the folders around it are the person's own and say nothing
        // the report needs.
        string Shorten(string path)
        {
            string localApp = f.Identity?.LocalAppData ?? "";
            string profile = f.Identity?.Profile ?? "";
            if (localApp.Length > 0 && path.StartsWith(localApp, StringComparison.OrdinalIgnoreCase))
            {
                path = "%LOCALAPPDATA%" + path[localApp.Length..];
            }
            else if (profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            {
                path = "%USERPROFILE%" + path[profile.Length..];
            }
            else if (!InProgramFiles(path))
            {
                path = OtherFolder + Path.GetFileName(path.TrimEnd('\\', '/'));
            }

            return Safe(path);
        }

        InstallFacts? install = f.Install;
        string? folder = string.IsNullOrEmpty(install?.Folder) ? null : Shorten(install.Folder);

        int trayCount = f.TrayProcesses ?? 0;

        w.WriteStartObject();
        w.WriteNumber("schema", 1);

        w.WriteStartObject("earshot");
        w.WriteBoolean("installed", install?.Installed == true);
        WriteStringOrNull(w, "folder", folder);
        WriteStringOrNull(w, "version", string.IsNullOrEmpty(install?.Version) ? null : Safe(install.Version));
        w.WriteBoolean("installedAppsEntry", install?.AppsEntry == true);
        WriteStringOrNull(w, "installedAppsVersion", string.IsNullOrEmpty(install?.AppsVersion) ? null : Safe(install.AppsVersion));
        w.WriteBoolean("startMenuShortcut", install?.StartMenuShortcut == true);
        w.WriteBoolean("runValue", install?.RunValue == true);
        w.WriteEndObject();

        // Where the sign-in start points. The flag's byte is raw: the format is undocumented, and bit 0x1 set is how the tray
        // reads "turned off in Windows".
        string? target = StartupFact.TargetOf(f.Startup?.RunCommand);
        w.WriteStartObject("startup");
        w.WriteBoolean("runValue", target is not null);
        WriteStringOrNull(w, "target", target is null ? null : Shorten(target));
        if (target is not null && f.Startup?.TargetExists is bool exists)
        {
            w.WriteBoolean("targetExists", exists);
        }
        else
        {
            w.WriteNull("targetExists");
        }

        string? flag = f.Startup?.ApprovedFlag;
        bool flagIsByte = flag is not null && ApprovedByte.IsMatch(flag);
        WriteStringOrNull(w, "approvedFlag", flagIsByte ? flag : null);
        if (flagIsByte)
        {
            w.WriteBoolean("turnedOffInWindows", (Convert.ToInt32(flag, 16) & 1) != 0);
        }
        else
        {
            w.WriteNull("turnedOffInWindows");
        }

        w.WriteEndObject();

        w.WriteStartObject("tray");
        w.WriteBoolean("running", trayCount > 0);
        w.WriteNumber("processes", trayCount);
        w.WriteEndObject();

        w.WriteStartArray("tasks");
        foreach (TaskFact t in f.Tasks ?? [])
        {
            w.WriteStartObject();
            WriteStringOrNull(w, "name", Safe(t.Name));
            w.WriteBoolean("present", t.Present);
            WriteStringOrNull(w, "state", string.IsNullOrEmpty(t.State) ? null : Safe(t.State));
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartObject("handBackService");
        w.WriteBoolean("present", f.Service?.Present == true);
        WriteStringOrNull(w, "state", string.IsNullOrEmpty(f.Service?.State) ? null : Safe(f.Service.State));
        w.WriteEndObject();

        int other = 0;
        w.WriteStartArray("airpods");
        foreach (DeviceFact d in f.Devices ?? [])
        {
            string? model = AirPodsModels.ModelOf(d.InstanceId);
            if (model is null)
            {
                continue;
            }

            if (model.Length == 0)
            {
                other++;
                continue;
            }

            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("bus", Safe(d.InstanceId.Split('\\')[0]));
            w.WriteBoolean("present", d.Present);
            WriteStringOrNull(w, "status", string.IsNullOrEmpty(d.Status) ? null : Safe(d.Status));
            if (d.Problem is int problem)
            {
                w.WriteNumber("problemCode", problem);
            }
            else
            {
                w.WriteNull("problemCode");
            }

            if (d.ConfigFlags is int flags)
            {
                w.WriteBoolean("disabled", (flags & 1) != 0);
            }
            else
            {
                w.WriteNull("disabled");
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteNumber("otherAppleNodes", other);

        w.WriteStartArray("bluetoothServices");
        foreach (BtServiceFact s in f.BtServices ?? [])
        {
            w.WriteStartObject();
            WriteStringOrNull(w, "name", Safe(s.Name));
            WriteStringOrNull(w, "state", string.IsNullOrEmpty(s.State) ? null : Safe(s.State));
            w.WriteEndObject();
        }

        w.WriteEndArray();

        RadioFact? radio = f.Radio;
        w.WriteStartObject("radio");
        w.WriteBoolean("found", radio?.Found == true);
        WriteBoolOrNull(w, "adapterFound", radio?.AdapterFound);
        WriteBoolOrNull(w, "classicSupported", radio?.ClassicSupported);
        WriteWordOrNull(w, "state", radio?.State);
        WriteWordOrNull(w, "radioState", radio?.RadioState);
        WriteStringOrNull(w, "devNodeStatus", radio?.DevNodeStatus is { } status && HexNumber.IsMatch(status) ? status : null);
        if (radio?.ProblemCode is int problemCode)
        {
            w.WriteNumber("problemCode", problemCode);
        }
        else
        {
            w.WriteNull("problemCode");
        }

        WriteStringOrNull(w, "driverVersion", string.IsNullOrEmpty(radio?.DriverVersion) ? null : Safe(radio.DriverVersion));
        WriteStringOrNull(w, "driverDate", radio?.DriverDate is { } driverDate && IsoDate.IsMatch(driverDate) ? driverDate : null);
        w.WriteStartArray("readCodes");
        foreach (string code in radio?.ReadCodes ?? [])
        {
            w.WriteStringValue(Safe(code.Length > 80 ? code[..80] : code));
        }

        w.WriteEndArray();
        w.WriteEndObject();

        w.WriteStartArray("filterDrivers");
        foreach (FilterFact x in f.Filters ?? [])
        {
            if (Array.IndexOf(InboxFilters, x.Name) >= 0)
            {
                continue;
            }

            w.WriteStartObject();
            WriteStringOrNull(w, "name", Safe(x.Name));
            WriteStringOrNull(w, "on", Safe(x.On));
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartObject("connectHistory");
        w.WriteString("path", HistoryPath);
        w.WriteBoolean("exists", f.History?.Exists == true);
        w.WriteNumber("lines", f.History?.Lines ?? 0);
        w.WriteStartArray("last");
        foreach (string line in f.History?.Last ?? [])
        {
            string? token = HistoryToken(line, known, user);
            if (token is not null)
            {
                w.WriteStringValue(token);
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();

        // The last pairing runs, as plain tokens; a value that is not one is dropped, so no free text from a writer survives.
        w.WriteStartArray("pairing");
        foreach (PairingFact run in (f.History?.Pairing ?? []).TakeLast(5))
        {
            w.WriteStartObject();
            WriteStringOrNull(w, "time", run.Time is { } time && IsoTime.IsMatch(time) ? time : null);
            WriteWordOrNull(w, "outcome", run.Outcome);
            WriteWordOrNull(w, "status", run.Status);
            if (run.StatusValue is long statusValue && statusValue is >= int.MinValue and <= uint.MaxValue)
            {
                w.WriteNumber("statusValue", statusValue);
            }
            else
            {
                w.WriteNull("statusValue");
            }

            WriteWordOrNull(w, "modelCheck", run.ModelCheck);
            if (run.DurationMs is long duration && duration is >= 0 and <= 86_400_000)
            {
                w.WriteNumber("durationMs", duration);
            }
            else
            {
                w.WriteNull("durationMs");
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartObject("protection");
        w.WriteBoolean("pinned", f.Protection?.Pinned == true);
        if (f.Protection is { } protection)
        {
            w.WriteNumber("unprotectedAirPods", protection.Unprotected);
        }
        else
        {
            w.WriteNull("unprotectedAirPods");
        }

        w.WriteEndObject();

        w.WriteStartObject("battery");
        w.WriteString("source", "proximity broadcast, Windows Hands-Free battery property");
        w.WriteBoolean("lastReadingSaved", f.LastReadingSaved == true);
        w.WriteEndObject();

        w.WriteStartArray("unreadable");
        foreach (UnreadableFact u in f.Unreadable)
        {
            w.WriteStartObject();
            WriteStringOrNull(w, "part", Safe(u.Part));
            WriteStringOrNull(w, "reason", Safe(u.Reason));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBoolOrNull(Utf8JsonWriter w, string name, bool? value)
    {
        if (value is bool b)
        {
            w.WriteBoolean(name, b);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    // A value kept only when it is one plain word, so free text under a word key never reaches the report.
    private static void WriteWordOrNull(Utf8JsonWriter w, string name, string? value) =>
        WriteStringOrNull(w, name, value is not null && OneWord.IsMatch(value) ? value : null);

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    // One line of the connect history, kept only as its plain tokens, so a name or an address a writer put in free text is never
    // carried. A line that is not a JSON object is dropped.
    private static string? HistoryToken(string line, List<string> known, string user)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (string key in HistoryKeys)
            {
                if (!doc.RootElement.TryGetProperty(key, out JsonElement value))
                {
                    continue;
                }

                // A number is kept only under a key that is a number, and only inside that key's range, so a 48-bit address
                // written as a number under any key (or a word key) never reaches the report. A word key keeps only text and
                // true or false.
                string? text;
                if (NumericKeys.TryGetValue(key, out (long Min, long Max) range))
                {
                    text = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= range.Min && number <= range.Max
                        ? number.ToString(CultureInfo.InvariantCulture)
                        : null;
                    if (key == "time" && value.ValueKind == JsonValueKind.String && IsoTime.IsMatch(value.GetString() ?? ""))
                    {
                        text = value.GetString();
                    }
                }
                else
                {
                    text = value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => null,
                    };
                }

                if (text is null)
                {
                    continue;
                }

                text = DiagnosticsRedactor.Redact(text, known, user);
                if (key == "reason" && !OneWord.IsMatch(text))
                {
                    text = "(text removed)";
                }

                if (text.Length > 80)
                {
                    text = text[..80];
                }

                parts.Add(key + "=" + text);
            }

            return string.Join(' ', parts);
        }
    }

    // The short plain summary that goes above the JSON.
    public static string Summary(JsonElement report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Earshot doctor (read only: nothing was changed and no device was contacted)");
        JsonElement earshot = report.GetProperty("earshot");
        if (earshot.GetProperty("installed").GetBoolean())
        {
            string running = report.GetProperty("tray").GetProperty("running").GetBoolean() ? "running" : "not running";
            string? version = earshot.GetProperty("version").GetString();
            sb.AppendLine("Earshot: installed" + (string.IsNullOrEmpty(version) ? "" : ", version " + version) + ", " + running + ".");
        }
        else
        {
            sb.AppendLine("Earshot: not installed.");
        }

        JsonElement airpods = report.GetProperty("airpods");
        int count = airpods.GetArrayLength();
        int present = airpods.EnumerateArray().Count(a => a.GetProperty("present").GetBoolean());
        sb.AppendLine(count == 0
            ? "AirPods in Windows: none found."
            : "AirPods in Windows: " + count.ToString(CultureInfo.InvariantCulture) + " entries, " + present.ToString(CultureInfo.InvariantCulture) + " present.");
        int otherApple = report.GetProperty("otherAppleNodes").GetInt32();
        if (otherApple > 0)
        {
            // Not named: an iPhone has the same vendor id as AirPods, and only two AirPods models are told apart by their id.
            sb.AppendLine(otherApple.ToString(CultureInfo.InvariantCulture) + " other Apple " + (otherApple == 1 ? "entry" : "entries") + ": an iPhone or AirPods of a model not listed.");
        }

        int bad = airpods.EnumerateArray().Count(a =>
            (a.GetProperty("problemCode").ValueKind == JsonValueKind.Number && a.GetProperty("problemCode").GetInt32() != 0) ||
            (a.GetProperty("disabled").ValueKind == JsonValueKind.True));
        if (bad > 0)
        {
            sb.AppendLine("AirPods entries with a problem code or switched off: " + bad.ToString(CultureInfo.InvariantCulture) + ".");
        }

        IEnumerable<string> services = report.GetProperty("bluetoothServices").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString() + " " + (s.GetProperty("state").GetString() ?? "missing"));
        sb.AppendLine("Bluetooth services: " + string.Join(", ", services) + ".");
        JsonElement protection = report.GetProperty("protection");
        if (protection.GetProperty("pinned").GetBoolean() && protection.GetProperty("unprotectedAirPods").ValueKind == JsonValueKind.Number && protection.GetProperty("unprotectedAirPods").GetInt32() > 0)
        {
            sb.AppendLine("Not protected: AirPods on this PC are not the ones Earshot protects (" + protection.GetProperty("unprotectedAirPods").GetInt32().ToString(CultureInfo.InvariantCulture) + "). Fix connection can set them up.");
        }

        JsonElement radio = report.GetProperty("radio");
        string? radioState = radio.GetProperty("state").GetString();
        if (radio.GetProperty("found").GetBoolean())
        {
            string problem = radio.GetProperty("problemCode").ValueKind == JsonValueKind.Number && radio.GetProperty("problemCode").GetInt32() != 0
                ? ", problem code " + radio.GetProperty("problemCode").GetInt32().ToString(CultureInfo.InvariantCulture)
                : "";
            string date = radio.GetProperty("driverDate").GetString() is { Length: > 0 } day ? " (" + day + ")" : "";
            sb.AppendLine("Bluetooth radio: " + (radioState ?? "state unknown") + problem + ", driver " + (radio.GetProperty("driverVersion").GetString() ?? "version unknown") + date + ".");
        }
        else if (radioState == "NoAdapter")
        {
            // Only a read that answered with no default adapter.
            sb.AppendLine("Bluetooth radio: none found.");
        }
        else
        {
            // The read did not finish, threw or was not made: nothing is said to be missing.
            sb.AppendLine("Bluetooth radio: not read" + (radioState is null ? "" : " (" + radioState + ")") + ".");
        }

        JsonElement startup = report.GetProperty("startup");
        if (startup.GetProperty("runValue").GetBoolean() && startup.GetProperty("targetExists").ValueKind == JsonValueKind.False)
        {
            sb.AppendLine("Open on startup: the Run value points at a file Earshot can't find.");
        }

        if (startup.GetProperty("turnedOffInWindows").ValueKind == JsonValueKind.True)
        {
            sb.AppendLine("Open on startup: turned off in Windows' startup settings.");
        }

        JsonElement pairing = report.GetProperty("pairing");
        if (pairing.GetArrayLength() > 0)
        {
            JsonElement last = pairing[pairing.GetArrayLength() - 1];
            sb.AppendLine("Last pairing: " + (last.GetProperty("outcome").GetString() ?? "unknown") + " (" + (last.GetProperty("status").GetString() ?? "no status") + ")" +
                (last.GetProperty("time").GetString() is { } at ? " at " + at : "") + ".");
        }

        List<string> filters = report.GetProperty("filterDrivers").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString() + " (" + x.GetProperty("on").GetString() + ")").ToList();
        sb.AppendLine(filters.Count > 0 ? "Filter drivers: " + string.Join(", ", filters) + "." : "Filter drivers: none.");
        JsonElement history = report.GetProperty("connectHistory");
        sb.AppendLine(history.GetProperty("exists").GetBoolean()
            ? "Connect history: " + history.GetProperty("lines").GetInt32().ToString(CultureInfo.InvariantCulture) + " lines."
            : "Connect history: none yet.");
        List<string> unreadable = report.GetProperty("unreadable").EnumerateArray().Select(u => u.GetProperty("part").GetString() ?? "").ToList();
        if (unreadable.Count > 0)
        {
            sb.AppendLine("Could not read: " + string.Join(", ", unreadable) + ".");
        }

        sb.AppendLine("Details for an AI follow. Names, addresses, device and container IDs, serial numbers, the user name and the PC name are removed.");
        return sb.ToString();
    }

    // The whole output of the command: the summary, then the JSON in a fenced block.
    public static string Output(DoctorFacts facts)
    {
        string json = Build(facts);
        using JsonDocument doc = JsonDocument.Parse(json);
        return Summary(doc.RootElement) + "```json" + Environment.NewLine + json + Environment.NewLine + "```" + Environment.NewLine;
    }
}

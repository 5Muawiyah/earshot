using System.Text;
using System.Text.RegularExpressions;

namespace Earshot.Diagnostics;

// Removes what could identify the owner, their machine or their devices from text that is about to be copied and
// pasted somewhere public: Bluetooth addresses, container ids and every other GUID, SIDs, device instance paths,
// user profile paths, and any name or title the caller lists (the paired device's friendly name, a window title,
// the signed-in user name). Each is replaced by a fixed tag, so the text still reads: <address>, <id>, <sid>,
// <device>, <path>, <name>.
//
// Pure: no I/O, no clock, no globals beyond the user name it is given or, by default, Environment.UserName.
// Idempotent: a tag contains nothing any rule matches, and a listed name is never replaced inside a tag, so
// Redact(Redact(x)) equals Redact(x).
//
// The shapes come from how this program writes them. A Bluetooth address is 48 bits, printed by the log as 12
// upper-case hex digits (Interop\BluetoothApis.FormatAddress12, the form used in BTHENUM instance ids) or as six
// pairs joined by ":" or "-", the form Windows' own tools use; with or without a 0x prefix is accepted. A container
// id is a GUID in the registry format. A SID is "S-1-" and numbers joined by "-"
// https://learn.microsoft.com/en-us/windows/win32/secauthz/security-identifiers
// and a device instance id starts with its enumerator, such as BTHENUM, BTHLE or SWD
// https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-instance-ids
// A device interface path is the same id with "#" in place of "\" and a trailing {class GUID}, and may be
// prefixed "\\?\".
// https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file
//
// Design choice: a twelve-digit run of hex digits is taken as an address even when all the digits are decimal.
// That may blank a harmless twelve-digit number; the alternative leaks about one address in 300.
public static partial class DiagnosticsRedactor
{
    public const string AddressTag = "<address>";
    public const string IdTag = "<id>";
    public const string SidTag = "<sid>";
    public const string DeviceTag = "<device>";
    public const string PathTag = "<path>";
    public const string NameTag = "<name>";

    // Chosen here so a one-letter name cannot blank a whole log: anything shorter is not removed by name.
    private const int MinimumNameLength = 2;

    private const string PathChars = @"[^\s""'<>|,;]";

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    // An enumerator prefix, optionally behind "\\?\" or "//?/", then the rest of the id up to a space or quote.
    // MMDEVAPI and HDAUDIO are the audio endpoint and codec enumerators the log can print.
    [GeneratedRegex(@"(?:\\\\\?\\|//\?/)?(?<![0-9A-Za-z])(?:BTHENUM|BTHLE|BTHHFENUM|BTH|SWD|USB|HDAUDIO|MMDEVAPI)[\\#]" + PathChars + "+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstancePath();

    // "{GUID}#..." as a device interface path ends in.
    [GeneratedRegex(@"\{" + GuidPattern + @"\}#" + PathChars + "*", RegexOptions.CultureInvariant)]
    private static partial Regex GuidHashPath();

    // "{0.0.0.00000000}.{GUID}", the id of an audio endpoint.
    [GeneratedRegex(@"\{\d\.\d\.\d\.[0-9a-fA-F]{8}\}\.\{" + GuidPattern + @"\}", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointId();

    // Bounded by "not a letter or digit" rather than \b, which sees "_" as part of a word and so skipped "ID_S-1-5-...".
    [GeneratedRegex(@"(?<![0-9A-Za-z])S-1-\d+(?:-\d+){1,14}(?![0-9A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sid();

    // A drive (any letter) or a share (\\server), "Users" or the older "Documents and Settings", a profile folder, and
    // whatever follows it. The profile folder is the whole segment up to the next separator, spaces included, when a
    // separator follows; with none (the name ends the text) it is the run up to a space, so the rest of a sentence is
    // not taken with it. A name with a space and no separator after it is caught by the listed name (see UserPath).
    [GeneratedRegex(@"(?:\\\\\?\\|//\?/)?(?:[A-Za-z]:|\\\\[^\\/\s""'<>|:*?]+)[\\/]+(?:Users|Documents and Settings)[\\/]+(?:[^\\/""'<>|:*?\r\n]+(?=[\\/])|[^\\/\s""'<>|:*?]+)(?:[\\/]+[^\s""'<>|:*?]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();

    [GeneratedRegex(@"%USERPROFILE%(?:[\\/]+[^\s""'<>|:*?]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfileVariable();

    // Bounded by "not a letter or digit" rather than \b, which sees "_" as part of a word and so skipped "ID_<guid>".
    [GeneratedRegex(@"\{?(?<![0-9A-Za-z])" + GuidPattern + @"(?![0-9A-Za-z])\}?", RegexOptions.CultureInvariant)]
    private static partial Regex GuidText();

    // A GUID written without hyphens (the "N" format).
    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-fA-F]{32}(?![0-9A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPlain();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}(?![0-9A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPairs();

    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:0[xX])?[0-9A-Fa-f]{12}(?![0-9A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex AddressDigits();

    // The lines the tray writes when a device is chosen or pinned carry the device's name, and the search string the owner
    // typed, which are not in the known names once the owner has chosen another device (only the one in use now is). They
    // are taken out by where they sit in the line, whatever they say. The shapes are those of TrayContext's three lines:
    //   Device chosen: <name> (<address>, container <id>), match "<text>".
    //   Device not changed to <name> (<address>): the boot block did not take it ...
    //   Pinned <name>: container <id>, address <address>.
    [GeneratedRegex(@"(Device chosen: ).+?( \([^()\r\n]*, container )", RegexOptions.CultureInvariant)]
    private static partial Regex ChosenName();

    [GeneratedRegex(@"(, match "")[^\r\n]*("")", RegexOptions.CultureInvariant)]
    private static partial Regex ChosenMatch();

    [GeneratedRegex(@"(Device not changed to ).+?( \([^()\r\n]*\): the boot block)", RegexOptions.CultureInvariant)]
    private static partial Regex NotChangedName();

    [GeneratedRegex(@"(Pinned ).+?(: container \S+, address )", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedName();

    // Removes everything listed above from text. knownNames are extra strings to remove wherever they appear, as a
    // whole word and ignoring case: the paired device's name, window titles. The signed-in user name
    // (Environment.UserName) is always removed too. A null text gives an empty string.
    public static string Redact(string? text, IEnumerable<string>? knownNames = null) =>
        Redact(text, knownNames, Environment.UserName);

    // The same with the user name given, so a test does not depend on who runs it. A null or blank userName removes
    // no user name.
    public static string Redact(string? text, IEnumerable<string>? knownNames, string? userName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = text;

        // The device names in the choose and pin lines first, before their addresses and ids are turned into tags.
        result = ChosenName().Replace(result, "$1" + NameTag + "$2");
        result = ChosenMatch().Replace(result, "$1" + NameTag + "$2");
        result = NotChangedName().Replace(result, "$1" + NameTag + "$2");
        result = PinnedName().Replace(result, "$1" + NameTag + "$2");

        // Paths with the profile folder first, so a user name that has a space in it is removed whole.
        result = UserPath(userName).Replace(result, PathTag);

        // Instance and interface paths before the pieces inside them (addresses, GUIDs) are taken out singly.
        result = InstancePath().Replace(result, DeviceTag);
        result = GuidHashPath().Replace(result, DeviceTag);
        result = EndpointId().Replace(result, IdTag);
        result = ProfilePath().Replace(result, PathTag);
        result = ProfileVariable().Replace(result, PathTag);
        result = Sid().Replace(result, SidTag);
        result = GuidText().Replace(result, IdTag);
        result = GuidPlain().Replace(result, IdTag);
        result = AddressPairs().Replace(result, AddressTag);
        result = AddressDigits().Replace(result, AddressTag);

        foreach (Regex name in NamePatterns(knownNames, userName))
        {
            result = name.Replace(result, NameEvaluator);
        }

        return result;
    }

    // Replaces a name, except where it is the word inside one of our own tags.
    private static string NameEvaluator(Match m)
    {
        string text = m.Result("$_");
        int end = m.Index + m.Length;
        bool insideTag = m.Index > 0 && text[m.Index - 1] == '<' && end < text.Length && text[end] == '>';
        return insideTag ? m.Value : NameTag;
    }

    // "C:\Users\<the user name>\..." when the user name is known; a name with a space or other character the general
    // pattern stops at is then taken whole. Never matches when there is no name.
    private static Regex UserPath(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Trim().Length < MinimumNameLength)
        {
            return NeverMatches;
        }

        string name = Regex.Escape(userName.Trim());
        return new Regex(@"(?:\\\\\?\\|//\?/)?[A-Za-z]:[\\/]+Users[\\/]+" + name + @"(?=[\\/\s""'<>|:*?,;]|$)(?:[\\/]+[^\s""'<>|:*?]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static readonly Regex NeverMatches = new(@"(?!)", RegexOptions.CultureInvariant);

    // One whole-word, case-insensitive pattern per name, longest first so a name that contains another is removed
    // whole. A name written with a curly apostrophe also matches the straight one, and the other way round, since the
    // device's name keeps U+2019 and a log line or a window title may not.
    private static List<Regex> NamePatterns(IEnumerable<string>? knownNames, string? userName)
    {
        var names = new List<string>();
        if (knownNames is not null)
        {
            names.AddRange(knownNames);
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            names.Add(userName);
        }

        var patterns = new List<Regex>();
        foreach (string raw in names.Select(n => n?.Trim() ?? string.Empty)
                     .Where(n => n.Length >= MinimumNameLength)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(n => n.Length))
        {
            var pattern = new StringBuilder();
            foreach (char c in raw)
            {
                pattern.Append(c is '\'' or '\u2019' ? "['\u2019]" : Regex.Escape(c.ToString()));
            }

            patterns.Add(new Regex(@"(?<![\p{L}\p{N}])" + pattern + @"(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }

        return patterns;
    }
}

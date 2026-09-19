using System.Globalization;

namespace Earshot.Hotkeys;

// The virtual-key codes a shortcut may name, and the canonical text for each. Every value is taken
// from https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes; none of the
// "Reserved", "Unassigned", "Undefined" or "OEM specific" gaps on that page are named here.
public static class VirtualKeyTable
{
    // Aliases accepted on input only; TryGetKeyName never returns one of these.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = "Enter",
        ["Back"] = "Backspace",
        ["Esc"] = "Escape",
        ["Ins"] = "Insert",
        ["Del"] = "Delete",
        ["PgUp"] = "PageUp",
        ["PgDn"] = "PageDown",
    };

    private static readonly (string Name, ushort VirtualKey)[] Entries = BuildEntries();

    private static readonly Dictionary<string, ushort> ByName =
        BuildByName(Entries);

    private static readonly Dictionary<ushort, string> ByVirtualKey =
        BuildByVirtualKey(Entries);

    // Canonical names, in a stable order, one per distinct virtual key.
    public static IReadOnlyList<string> KeyNames { get; } = Array.ConvertAll(Entries, e => e.Name);

    public static bool TryGetVirtualKey(string keyName, out ushort virtualKey)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        string canonical = Aliases.TryGetValue(keyName, out string? target) ? target : keyName;
        return ByName.TryGetValue(canonical, out virtualKey);
    }

    public static bool TryGetKeyName(ushort virtualKey, out string keyName)
    {
        if (ByVirtualKey.TryGetValue(virtualKey, out string? name))
        {
            keyName = name;
            return true;
        }

        keyName = string.Empty;
        return false;
    }

    private static Dictionary<string, ushort> BuildByName((string Name, ushort VirtualKey)[] entries)
    {
        var map = new Dictionary<string, ushort>(entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ushort virtualKey) in entries)
        {
            map[name] = virtualKey;
        }

        return map;
    }

    private static Dictionary<ushort, string> BuildByVirtualKey((string Name, ushort VirtualKey)[] entries)
    {
        var map = new Dictionary<ushort, string>(entries.Length);
        foreach ((string name, ushort virtualKey) in entries)
        {
            map[virtualKey] = name;
        }

        return map;
    }

    private static (string Name, ushort VirtualKey)[] BuildEntries()
    {
        var entries = new List<(string, ushort)>(96);
        for (int letter = 0; letter < 26; letter++)
        {
            entries.Add((((char)('A' + letter)).ToString(), (ushort)(0x41 + letter)));
        }

        for (int digit = 0; digit <= 9; digit++)
        {
            entries.Add((digit.ToString(CultureInfo.InvariantCulture), (ushort)(0x30 + digit)));
        }

        for (int fn = 1; fn <= 24; fn++)
        {
            entries.Add(("F" + fn.ToString(CultureInfo.InvariantCulture), (ushort)(0x70 + fn - 1)));
        }

        for (int numpad = 0; numpad <= 9; numpad++)
        {
            entries.Add(("Numpad" + numpad.ToString(CultureInfo.InvariantCulture), (ushort)(0x60 + numpad)));
        }

        entries.AddRange(
        [
            ("Space", (ushort)0x20),
            ("Tab", (ushort)0x09),
            ("Enter", (ushort)0x0D),
            ("Backspace", (ushort)0x08),
            ("Escape", (ushort)0x1B),
            ("Insert", (ushort)0x2D),
            ("Delete", (ushort)0x2E),
            ("Home", (ushort)0x24),
            ("End", (ushort)0x23),
            ("PageUp", (ushort)0x21),
            ("PageDown", (ushort)0x22),
            ("Left", (ushort)0x25),
            ("Right", (ushort)0x27),
            ("Up", (ushort)0x26),
            ("Down", (ushort)0x28),
            ("Plus", (ushort)0xBB),
            ("Minus", (ushort)0xBD),
            ("Comma", (ushort)0xBC),
            ("Period", (ushort)0xBE),
        ]);

        return [.. entries];
    }
}

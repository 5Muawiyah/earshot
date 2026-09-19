namespace Earshot.Hotkeys;

// Parses and formats shortcut text such as "Ctrl+Alt+P". Matching is always
// StringComparison.OrdinalIgnoreCase: the current culture must never decide whether "Win" parses,
// because a Turkish "i" is not the ASCII one.
// https://learn.microsoft.com/en-us/dotnet/api/system.stringcomparison
public static class HotkeyText
{
    public const string EmptyInputMessage = "Type a shortcut, such as Ctrl+Alt+P.";
    public const string NoModifierMessage = "A shortcut needs Ctrl, Alt, Shift or Win as well as a key.";
    public const string NoKeyMessage = "A shortcut needs a key as well as a modifier.";
    public const string TwoKeysMessage = "A shortcut can have only one key besides the modifiers.";
    public const string KeyNotLastMessage = "Put the key last, as in Ctrl+Alt+P.";
    public const string EmptySegmentMessage = "That shortcut has an empty part. Use a form like Ctrl+Alt+P.";

    /// <summary>Parses text such as "Ctrl+Alt+P".</summary>
    /// <returns>true on success. On failure, <paramref name="error"/> is one of the parse-error
    /// strings above and <paramref name="combination"/> is default.</returns>
    public static bool TryParse(string? text, out HotkeyCombination combination, out string error)
    {
        combination = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = EmptyInputMessage;
            return false;
        }

        string[] rawSegments = text.Split('+');
        var segments = new string[rawSegments.Length];
        for (int i = 0; i < rawSegments.Length; i++)
        {
            segments[i] = rawSegments[i].Trim();
            if (segments[i].Length == 0)
            {
                error = EmptySegmentMessage;
                return false;
            }
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int keyIndex = -1;
        ushort virtualKey = 0;

        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (TryMatchModifier(segment, out HotkeyModifiers flag))
            {
                if ((modifiers & flag) != 0)
                {
                    error = "\"" + segment + "\" is listed twice.";
                    return false;
                }

                modifiers |= flag;
                continue;
            }

            if (VirtualKeyTable.TryGetVirtualKey(segment, out ushort vk))
            {
                if (keyIndex >= 0)
                {
                    error = TwoKeysMessage;
                    return false;
                }

                keyIndex = i;
                virtualKey = vk;
                continue;
            }

            error = "There is no key called \"" + segment + "\".";
            return false;
        }

        if (keyIndex < 0)
        {
            error = NoKeyMessage;
            return false;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = NoModifierMessage;
            return false;
        }

        if (keyIndex != segments.Length - 1)
        {
            error = KeyNotLastMessage;
            return false;
        }

        combination = new HotkeyCombination(modifiers, virtualKey);
        error = string.Empty;
        return true;
    }

    /// <summary>Canonical text: Ctrl, then Alt, then Shift, then Win, then the key.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The virtual key has no name.</exception>
    public static string Format(HotkeyCombination combination)
    {
        if (!VirtualKeyTable.TryGetKeyName(combination.VirtualKey, out string keyName))
        {
            throw new ArgumentOutOfRangeException(nameof(combination), combination.VirtualKey, "The virtual key has no name.");
        }

        var parts = new List<string>(5);
        if ((combination.Modifiers & HotkeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((combination.Modifiers & HotkeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((combination.Modifiers & HotkeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((combination.Modifiers & HotkeyModifiers.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join('+', parts);
    }

    public static bool TryFormat(HotkeyCombination combination, out string text)
    {
        if (!VirtualKeyTable.TryGetKeyName(combination.VirtualKey, out _))
        {
            text = string.Empty;
            return false;
        }

        text = Format(combination);
        return true;
    }

    private static bool TryMatchModifier(string segment, out HotkeyModifiers modifier)
    {
        if (segment.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || segment.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Control;
            return true;
        }

        if (segment.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Alt;
            return true;
        }

        if (segment.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Shift;
            return true;
        }

        if (segment.Equals("Win", StringComparison.OrdinalIgnoreCase) || segment.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            modifier = HotkeyModifiers.Windows;
            return true;
        }

        modifier = HotkeyModifiers.None;
        return false;
    }
}

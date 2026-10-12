using System.Globalization;
using System.Text;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Widget;

namespace Earshot.Commands;

// The settings the settings page offers that can be changed from a command line, by name. Each one is checked the way the
// page checks it: the same lists (gauge positions and orders, the case-open card's close choices, the low battery steps), the
// same label cleaning, and the shortcut model that refuses a chord Windows reserves or another command already holds.
//
// Left out on purpose: Hand back and the Hands-Free microphone mode, which change Windows (a service's configuration, the
// AirPods' nodes) and are done from the menu; the gauge's display, which is a display's identity, not a word.
internal static class SettingsCatalog
{
    internal const string OffWord = "off";
    internal const string OnWord = "on";
    internal const string NoneWord = "none";
    internal const string UntilCaseCloses = "until-case-closes";

    internal sealed record SettingDefinition(string Name, string Accepts, Func<EarshotSettings, string> Read, Func<EarshotSettings, string, string?> Write);

    public static IReadOnlyList<SettingDefinition> All { get; } = Build();

    public const string NotChangedHere = "Not changed here: hand back at shut down, the Hands-Free microphone mode and the gauge display. Use Earshot's menu or settings page.";

    public static SettingDefinition? Find(string name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string Names() => string.Join(", ", All.Select(d => d.Name));

    private static List<SettingDefinition> Build()
    {
        var list = new List<SettingDefinition>
        {
            Choice("gauge-position", Enum.GetValues<GaugePosition>(),
                s => s.Widget.GaugePosition, (s, v) => s.Widget = s.Widget with { GaugePosition = v }),
            Choice("gauge-order", Enum.GetValues<GaugeOrder>(),
                s => s.Widget.GaugeOrder, (s, v) => s.Widget = s.Widget with { GaugeOrder = v }),
            Text("other-device-name", "text of up to " + WidgetSettings.MaxOtherDeviceLabelLength.ToString(CultureInfo.InvariantCulture) + " characters, or empty",
                s => s.Widget.OtherDeviceLabel, OtherDeviceName),
            Switch("pause-when-bud-out", s => s.Widget.AutoPause, (s, on) => s.Widget = (s.Widget with { AutoPause = on }).WithWatcherRecomputed()),
            Switch("pause-when-airpods-leave", s => s.PauseWhenAirPodsLeave, (s, on) => s.PauseWhenAirPodsLeave = on),
            Switch("low-battery-alerts", s => s.Widget.LowBatteryAlert, (s, on) => s.Widget = (s.Widget with { LowBatteryAlert = on }).WithWatcherRecomputed()),
            new("low-battery-percent",
                CardSettingsValues.LowBatteryMin.ToString(CultureInfo.InvariantCulture) + " to " + CardSettingsValues.LowBatteryMax.ToString(CultureInfo.InvariantCulture) +
                    " in steps of " + CardSettingsValues.LowBatteryStep.ToString(CultureInfo.InvariantCulture),
                s => s.Widget.LowBatteryThresholdPercent.ToString(CultureInfo.InvariantCulture),
                LowBatteryPercent),
            Switch("fully-charged-notice", s => s.Widget.FullyChargedNotice, (s, on) => s.Widget = (s.Widget with { FullyChargedNotice = on }).WithWatcherRecomputed()),
            Switch("left-click-connects", s => s.Widget.LeftClickConnects, (s, on) => s.Widget = s.Widget with { LeftClickConnects = on }),
            Switch("case-open-card", s => s.Widget.CaseOpenCardOn, (s, on) => s.Widget = (s.Widget with { CaseOpenCardOn = on }).WithWatcherRecomputed()),
            new("case-open-card-close",
                UntilCaseCloses + ", " + string.Join(", ", CaseOpenCardClose.Choices.Where(c => c != CaseOpenCardClose.UntilCaseCloses).Select(c => c.ToString(CultureInfo.InvariantCulture))) + " (seconds)",
                s => s.Widget.CaseOpenCardCloseSeconds == CaseOpenCardClose.UntilCaseCloses ? UntilCaseCloses : s.Widget.CaseOpenCardCloseSeconds.ToString(CultureInfo.InvariantCulture),
                CaseOpenCardCloseSeconds),
            Switch("check-automatically", s => s.CheckForUpdatesAutomatically, (s, on) => s.CheckForUpdatesAutomatically = on),
            Shortcut("shortcut-connect", HotkeyAction.SwitchToPc),
            Shortcut("shortcut-disconnect", HotkeyAction.SwitchToPhone),
            Shortcut("shortcut-card", HotkeyAction.OpenCard),
        };
        return list;
    }

    // Words are the enum names in lower case with hyphens: RightEnd is right-end.
    private static string Kebab(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsUpper(c) && sb.Length > 0)
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static SettingDefinition Choice<T>(string name, T[] values, Func<EarshotSettings, T> read, Action<EarshotSettings, T> write)
        where T : struct, Enum =>
        new(name,
            string.Join(", ", values.Select(v => Kebab(v.ToString()))),
            s => Kebab(read(s).ToString()),
            (s, text) =>
            {
                foreach (T v in values)
                {
                    if (string.Equals(Kebab(v.ToString()), text, StringComparison.OrdinalIgnoreCase))
                    {
                        write(s, v);
                        return null;
                    }
                }

                return "\"" + text + "\" is not one of: " + string.Join(", ", values.Select(v => Kebab(v.ToString()))) + ".";
            });

    private static SettingDefinition Text(string name, string accepts, Func<EarshotSettings, string> read, Func<EarshotSettings, string, string?> write) =>
        new(name, accepts, read, write);

    private static SettingDefinition Switch(string name, Func<EarshotSettings, bool> read, Action<EarshotSettings, bool> write) =>
        new(name, OnWord + " or " + OffWord, s => read(s) ? OnWord : OffWord, (s, text) =>
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "on" or "true" or "yes" or "1":
                    write(s, true);
                    return null;
                case "off" or "false" or "no" or "0":
                    write(s, false);
                    return null;
                default:
                    return "\"" + text + "\" is not on or off.";
            }
        });

    private static string? OtherDeviceName(EarshotSettings s, string text)
    {
        if (text.Length > WidgetSettings.MaxOtherDeviceLabelLength)
        {
            return "The name is longer than " + WidgetSettings.MaxOtherDeviceLabelLength.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        string cleaned = WidgetSettings.CleanedLabel(text);
        if (!string.Equals(cleaned, text.Trim(), StringComparison.Ordinal))
        {
            return "The name has characters that cannot be used.";
        }

        s.Widget = s.Widget with { OtherDeviceLabel = cleaned };
        return null;
    }

    private static string? LowBatteryPercent(EarshotSettings s, string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) ||
            percent < CardSettingsValues.LowBatteryMin || percent > CardSettingsValues.LowBatteryMax || percent % CardSettingsValues.LowBatteryStep != 0)
        {
            return "\"" + text + "\" is not a multiple of " + CardSettingsValues.LowBatteryStep.ToString(CultureInfo.InvariantCulture) + " from " +
                CardSettingsValues.LowBatteryMin.ToString(CultureInfo.InvariantCulture) + " to " + CardSettingsValues.LowBatteryMax.ToString(CultureInfo.InvariantCulture) + ".";
        }

        s.Widget = s.Widget with { LowBatteryThresholdPercent = percent };
        return null;
    }

    private static string? CaseOpenCardCloseSeconds(EarshotSettings s, string text)
    {
        int seconds;
        if (string.Equals(text, UntilCaseCloses, StringComparison.OrdinalIgnoreCase))
        {
            seconds = CaseOpenCardClose.UntilCaseCloses;
        }
        else if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds == CaseOpenCardClose.UntilCaseCloses || !CaseOpenCardClose.IsChoice(seconds))
        {
            return "\"" + text + "\" is not one of: " + UntilCaseCloses + ", " +
                string.Join(", ", CaseOpenCardClose.Choices.Where(c => c != CaseOpenCardClose.UntilCaseCloses).Select(c => c.ToString(CultureInfo.InvariantCulture))) + ".";
        }

        s.Widget = s.Widget with { CaseOpenCardCloseSeconds = seconds };
        return null;
    }

    private static SettingDefinition Shortcut(string name, HotkeyAction action) =>
        new(name, "a chord such as Ctrl+Alt+Shift+A, or " + NoneWord,
            s => s.Hotkeys.TextFor(action) is { Length: > 0 } chord ? chord : NoneWord,
            (s, text) =>
            {
                var bindings = new HotkeyBindingModel(s.Hotkeys, () => []);
                if (string.Equals(text, NoneWord, StringComparison.OrdinalIgnoreCase))
                {
                    bindings.Clear(action);
                    return null;
                }

                return bindings.Set(action, text);
            });
}

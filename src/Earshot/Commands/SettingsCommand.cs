using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.Commands;

// Earshot.exe settings list | get <name> | set <name> <value>
//
// Writes the same settings file the tray uses, through the same store, which saves it atomically. A tray that is running
// watches that file (SettingsFileWatcher) and takes the change up within a moment.
internal static class SettingsCommand
{
    internal const string Usage = "Usage: Earshot.exe settings list | get <name> | set <name> <value>";

    // args are the words after "settings". Returns the process exit code: 0, ExitCodes.Usage for a bad name, value or line, or
    // ExitCodes.Refused when the file belongs to a newer Earshot and nothing can be saved.
    public static int Run(IReadOnlyList<string> args, JsonSettingsStore store, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(output);

        string verb = args.Count > 0 ? args[0] : "";
        switch (verb)
        {
            case "list" when args.Count == 1:
                return List(store, output);
            case "get" when args.Count == 2:
                return Get(store, args[1], output);
            case "set" when args.Count == 3:
                return Set(store, args[1], args[2], output);
            default:
                output.WriteLine(Usage);
                return ExitCodes.Usage;
        }
    }

    private static int List(JsonSettingsStore store, TextWriter output)
    {
        EarshotSettings current = store.Current;
        foreach (SettingsCatalog.SettingDefinition d in SettingsCatalog.All)
        {
            output.WriteLine(d.Name + " = " + d.Read(current) + "  (" + d.Accepts + ")");
        }

        output.WriteLine(SettingsCatalog.NotChangedHere);
        return ExitCodes.Ok;
    }

    private static int Get(JsonSettingsStore store, string name, TextWriter output)
    {
        SettingsCatalog.SettingDefinition? def = SettingsCatalog.Find(name);
        if (def is null)
        {
            return Unknown(name, output);
        }

        output.WriteLine(def.Read(store.Current));
        return ExitCodes.Ok;
    }

    private static int Set(JsonSettingsStore store, string name, string value, TextWriter output)
    {
        SettingsCatalog.SettingDefinition? def = SettingsCatalog.Find(name);
        if (def is null)
        {
            return Unknown(name, output);
        }

        // The value is tried on a copy first, so a refused value changes and saves nothing.
        EarshotSettings scratch = store.Current;
        string? problem = def.Write(scratch, value);
        if (problem is not null)
        {
            output.WriteLine("Not changed. " + problem + " " + def.Name + " accepts: " + def.Accepts + ".");
            return ExitCodes.Usage;
        }

        if (store.IsReadOnly)
        {
            output.WriteLine("Not changed. The settings file is from a newer version of Earshot, so this one leaves it alone.");
            return ExitCodes.Refused;
        }

        try
        {
            store.Update(s => def.Write(s, value));
        }
        catch (ArgumentException ex)
        {
            output.WriteLine("Not changed. " + ex.Message);
            return ExitCodes.Usage;
        }

        output.WriteLine(def.Name + " is now " + def.Read(store.Current) + ".");
        return ExitCodes.Ok;
    }

    private static int Unknown(string name, TextWriter output)
    {
        output.WriteLine("There is no setting called \"" + name + "\". The settings are: " + SettingsCatalog.Names() + ".");
        return ExitCodes.Usage;
    }
}

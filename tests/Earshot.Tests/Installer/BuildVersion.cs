using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Earshot.Tests.Installer;

// The release version of the program the test build produced, written once in the application's project file. The
// installer script reads an installed program's version from the file itself, and a world that installs the test build's own
// program (InstallerWorld.InstallRealProgram) therefore has this version installed. A fixture that means "the installed
// version" or "a release newer than the installed one" is written from here, so the next version bump does not break it.
internal static partial class BuildVersion
{
    // major.minor.patch, read from the program file the way the script reads it: the product version without the build
    // metadata that follows a plus sign.
    public static string Current { get; } = Read();

    public static string CurrentTag => "v" + Current;

    // The next patch release after this build: a release the installed program is older than.
    public static string Newer { get; } = Next(Current);

    public static string NewerTag => "v" + Newer;

    private static string Read()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        string text = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty;
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        Match match = ReleaseVersionPattern().Match(text);
        if (!match.Success)
        {
            throw new InvalidOperationException("The test build's program reports the version '" + text + "', which is not major.minor.patch.");
        }

        return match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value;
    }

    private static string Next(string version)
    {
        string[] parts = version.Split('.');
        return parts[0] + "." + parts[1] + "." + (int.Parse(parts[2], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(\.\d{1,9})?$")]
    private static partial Regex ReleaseVersionPattern();
}

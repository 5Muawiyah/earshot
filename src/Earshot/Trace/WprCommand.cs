namespace Earshot.Tracing;

// The wpr.exe command lines. Windows Performance Recorder command line:
// https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options
//
// -start "<profile file>!<profile id>" -filemode, -stop <file> and -cancel were run on this PC on 25 October 2026. Starting
// and stopping need administrator, and a non-elevated -status does not see an elevated session.
internal static class WprCommand
{
    public static string ExePath(string systemRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemRoot);
        if (!Path.IsPathFullyQualified(systemRoot))
        {
            throw new ArgumentException("The Windows folder must be a full path.", nameof(systemRoot));
        }

        return Path.Combine(systemRoot, "System32", "wpr.exe");
    }

    public static IReadOnlyList<string> Start(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        return ["-start", profilePath + "!" + TraceProfile.ProfileId, "-filemode"];
    }

    public static IReadOnlyList<string> Stop(string etlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etlPath);
        return ["-stop", etlPath];
    }

    public static IReadOnlyList<string> Cancel { get; } = ["-cancel"];

    public static IReadOnlyList<string> Help { get; } = ["-help"];
}

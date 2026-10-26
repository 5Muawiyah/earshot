namespace Earshot.Tracing;

// The wpr.exe command lines. Windows Performance Recorder command line:
// https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options
//
// -start "<profile file>!<profile id>" -filemode, -stop <file> and -cancel were run on this PC on 25 October 2026. Starting
// and stopping need administrator, and a non-elevated -status does not see an elevated session.
//
// Two options from the same page keep the recording Earshot's own. -recordtempto names the folder the recording is written to
// before it is saved, so the file being recorded sits in the run folder only administrators can open instead of the user's
// %TEMP%. -instancename names the session; the page says it must be the last parameter and that every later command for the
// session must give the same name, so -stop and -cancel can only reach the session Earshot started, never another program's.
internal static class WprCommand
{
    internal const string InstanceName = "EarshotBluetoothTrace";

    public static string ExePath(string systemRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemRoot);
        if (!Path.IsPathFullyQualified(systemRoot))
        {
            throw new ArgumentException("The Windows folder must be a full path.", nameof(systemRoot));
        }

        return Path.Combine(systemRoot, "System32", "wpr.exe");
    }

    public static IReadOnlyList<string> Start(string profilePath, string recordFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordFolder);
        if (!Path.IsPathFullyQualified(recordFolder))
        {
            throw new ArgumentException("The recording folder must be a full path.", nameof(recordFolder));
        }

        return ["-start", profilePath + "!" + TraceProfile.ProfileId, "-filemode", "-recordtempto", recordFolder, "-instancename", InstanceName];
    }

    public static IReadOnlyList<string> Stop(string etlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etlPath);
        return ["-stop", etlPath, "-instancename", InstanceName];
    }

    public static IReadOnlyList<string> Cancel { get; } = ["-cancel", "-instancename", InstanceName];

    public static IReadOnlyList<string> Help { get; } = ["-help"];
}

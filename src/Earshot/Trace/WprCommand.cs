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
// session must give the same name, so -stop and -cancel should reach only the session Earshot started. That is read from the
// page and not yet seen in a real recording (verification.md).
internal static class WprCommand
{
    // With the terminal services session in it, so a cancel before a start reaches only this session's Earshot recording, not
    // one another signed-in user's Earshot is making.
    internal static string InstanceName { get; } = InstanceNameFor(System.Diagnostics.Process.GetCurrentProcess().SessionId);

    internal static string InstanceNameFor(int sessionId) => "EarshotBluetoothTraceS" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // systemDirectory: GetSystemDirectory's answer, not a path built from an environment variable.
    // https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemdirectoryw
    public static string ExePath(string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        if (!Path.IsPathFullyQualified(systemDirectory))
        {
            throw new ArgumentException("The system folder must be a full path.", nameof(systemDirectory));
        }

        return Path.Combine(systemDirectory, "wpr.exe");
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

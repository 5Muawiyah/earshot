using System.Diagnostics;

namespace Earshot.Setup;

// What Setup runs and how: the programs inside the staging folder, with the exit code they end with. Behind an interface so no
// test starts one for real.
internal interface IProcessLauncher
{
    // Runs the program with these arguments (no shell, no command line parsing of a joined string) and waits for it. -1 and a
    // reason when it could not be started or did not end in time.
    LaunchResult RunAndWait(string exePath, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout);

    // Asks Explorer to open a program, which starts it in the signed-in user's own session and not elevated, and does not
    // wait. That Explorer hands an opened path to the running shell is how it behaves, not something Windows documents, so
    // the result only decides what the last message says; nothing depends on it.
    LaunchResult StartInUserSession(string exePath);
}

internal readonly record struct LaunchResult(bool Started, int ExitCode, string Detail);

internal sealed class WindowsProcessLauncher : IProcessLauncher
{
    public LaunchResult RunAndWait(string exePath, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Setup was started from the person's session, whose environment any program they run can change. The programs it runs
        // are elevated, so the variables that make the .NET host or runtime load other code are not passed on.
        foreach (string name in start.Environment.Keys.Where(IsRuntimeVariable).ToList())
        {
            start.Environment.Remove(name);
        }

        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return new LaunchResult(false, -1, "The program did not start: " + exePath);
            }

            if (!process.WaitForExit(timeout))
            {
                return new LaunchResult(true, -1, "The program was still running after " + timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
            }

            return new LaunchResult(true, process.ExitCode, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            int code = ex is System.ComponentModel.Win32Exception w ? w.NativeErrorCode : ex.HResult;
            return new LaunchResult(false, -1, "0x" + code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ": " + ex.Message);
        }
    }

    // The variables the .NET host and runtime read to load code, change which runtime runs or write files: all of the DOTNET_,
    // COMPlus_, COR_ and CORECLR_ families.
    // https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables
    internal static bool IsRuntimeVariable(string name) =>
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase);

    public LaunchResult StartInUserSession(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(exePath);
        try
        {
            using Process? process = Process.Start(start);
            return new LaunchResult(process is not null, 0, process is null ? "Explorer did not start." : "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new LaunchResult(false, -1, ex.Message);
        }
    }
}

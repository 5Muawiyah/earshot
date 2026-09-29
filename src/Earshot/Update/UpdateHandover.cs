using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Earshot.Boot;
using Earshot.Contracts;

namespace Earshot.Update;

// Who install sets up for: the same three values the tray's "Set up Earshot..." passes, because the update is that
// setup run again from the new files. Install refuses a run without them.
internal sealed record HandoverIdentity(string UserSid, string Address, Guid ContainerId);

internal enum LaunchOutcome
{
    Started,
    Declined,
    Failed,
}

// Process is set when the program started; the caller disposes it. Win32Error is the raw code of a failed start.
internal sealed record LaunchResult(LaunchOutcome Outcome, uint Win32Error, string Detail, Process? Process);

// Starts the staged Earshot.exe. The real one asks Windows for the administrator prompt; a test gives the
// controller a fake so no test ever elevates.
internal interface IUpdateLauncher
{
    LaunchResult Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory);
}

internal static class UpdateHandover
{
    // The verb setup accepts for a first install and for a repair alike.
    internal const string InstallVerb = "install";

    // The pinned device, in the form install checks: a user SID, twelve upper-case hex digits, and the container.
    // Null with the reason when any of them is missing or malformed, which is the state before setup has chosen a
    // device: install refuses those too, so this asks before anything is downloaded.
    internal static HandoverIdentity? TryIdentity(string? userSid, EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!BoundaryValidation.IsAddress12(settings.PinnedAddress) || !NodeMatch.IsValidTargetContainer(settings.PinnedContainerId) || !Sddl.IsUserSid(userSid))
        {
            return null;
        }

        return new HandoverIdentity(userSid!, settings.PinnedAddress, settings.PinnedContainerId);
    }

    // install <userSid> <address> <containerGuid>, exactly as BlockController.RunSetupAsync builds it.
    internal static IReadOnlyList<string> InstallArguments(HandoverIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return [InstallVerb, identity.UserSid, identity.Address, identity.ContainerId.ToString("D", CultureInfo.InvariantCulture)];
    }

    // One command line for CreateProcess: each argument as CommandLineToArgvW reads it back. Nothing here is a shell
    // command; the arguments are checked values (a SID, hex, a GUID), and quoting is still done properly.
    // https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments
    internal static string JoinArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var line = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }

            line.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    line.Append('\\', (backslashes * 2) + 1).Append('"');
                    backslashes = 0;
                }
                else
                {
                    line.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                }
            }

            line.Append('\\', backslashes * 2).Append('"');
        }

        return line.ToString();
    }
}

// ShellExecute with the runas verb, which shows the one administrator prompt. It does not wait: the program that
// starts has to replace the folder this process is running from, so this process must be free to end. A declined
// prompt is ERROR_CANCELLED (1223).
// https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.verb
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecuteexw
internal sealed class ElevatedUpdateLauncher : IUpdateLauncher
{
    internal const uint ErrorCancelled = 1223;

    private readonly string? _verb;
    private readonly bool _hidden;

    // The real launcher: the runas verb, a normal window.
    public ElevatedUpdateLauncher()
        : this("runas", hidden: false)
    {
    }

    // For tests: no verb (the program simply runs, never elevated) and optionally a hidden window, so a console
    // program such as cmd.exe shows nothing on the desktop.
    internal ElevatedUpdateLauncher(string? verb, bool hidden)
    {
        _verb = verb;
        _hidden = hidden;
    }

    public LaunchResult Launch(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo info = CreateStartInfo(executable, arguments, workingDirectory);
        try
        {
            Process? process = Process.Start(info);
            return process is null
                ? new LaunchResult(LaunchOutcome.Failed, 0, "No process was started for " + executable + ".", null)
                : new LaunchResult(LaunchOutcome.Started, 0, executable + " " + info.Arguments, process);
        }
        catch (Win32Exception ex)
        {
            uint code = unchecked((uint)ex.NativeErrorCode);
            return new LaunchResult(code == ErrorCancelled ? LaunchOutcome.Declined : LaunchOutcome.Failed, code,
                "Win32 error " + code.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message, null);
        }
        catch (InvalidOperationException ex)
        {
            return new LaunchResult(LaunchOutcome.Failed, 0, ex.GetType().Name + ": " + ex.Message, null);
        }
    }

    internal ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Arguments = UpdateHandover.JoinArguments(arguments),
            WorkingDirectory = workingDirectory,
            WindowStyle = _hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        if (_verb is not null)
        {
            info.Verb = _verb;
        }

        return info;
    }
}

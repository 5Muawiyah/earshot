using Earshot.Boot;
using Earshot.Interop;

namespace Earshot.Service;

// Everything install registers for the hand-back service, as one value: the values it applies, the read-back
// check and the probe all take them from here, so the three cannot drift.
internal sealed record ServiceSpec(
    string Name,
    string DisplayName,
    string Description,
    string ImagePath,
    uint ServiceType,
    uint StartType,
    uint ErrorControl,
    string Account,
    uint PreshutdownTimeoutMs,
    string Sddl,
    bool DelayedAutoStart = false,
    uint ServiceSidType = 0);

// The one always-on part of Earshot: a LocalSystem service that, when Windows starts to shut down, blocks the
// pinned AirPods if the tray icon did not already hand them back. Registered by install, removed by uninstall.
//
//   Name             no slash or backslash, and not the product name, so a later service is not "the" Earshot service
//   Image path       the quoted install-folder executable and the literal argument "service"; the argument only picks
//                    the run mode and carries no data. Under Program Files, so only an administrator can change it
//   Type             its own process, never interactive: a service must not show a window
//   Start            automatic, so the service is already running when the preshutdown control comes; not delayed,
//                    because nothing in it waits on anything
//   Account          Local System (no account name and no password at creation)
//   Pre-shutdown     10,000 ms, the documented default since Windows 10 build 15063, set explicitly so the read-back
//                    checks it and a later change of the default cannot shorten it unseen
//   Access list      SYSTEM and Administrators full control; authenticated users read the service and nothing more
//                    (0x2000D: READ_CONTROL, SERVICE_QUERY_CONFIG, SERVICE_QUERY_STATUS, SERVICE_ENUMERATE_DEPENDENTS).
//                    Nobody but an administrator holds start, stop, pause, interrogate, a user-defined control or the
//                    right to change the configuration. The default list would give every user interrogate and the
//                    user-defined controls.
//   Not set          failure actions (a queued restart cannot be cancelled and would fight uninstall), required
//                    privileges (the privileges the device call needs are not documented), a service SID, triggers,
//                    delayed start, dependencies and a load order group. "Not set" is checked, not assumed: the
//                    read-back reads each of them, and install clears the ones it can when a registration from an
//                    earlier install has them. Required privileges are only read: what an empty list would mean to the
//                    control manager is not documented, and a list that cut the token down could stop the device call.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-createservicew
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_preshutdown_info
// https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights
internal static class ServicePlan
{
    public const string ServiceName = "EarshotHandBack";
    public const string DisplayName = "Earshot hand-back";
    public const string Description = "Hands the AirPods back when this computer shuts down, if the Earshot tray icon did not.";

    // The literal argument after the image path. Dispatch selects the service run mode from it.
    public const string RunModeArgument = "service";

    public const string AccountName = "LocalSystem";

    public const uint PreshutdownTimeoutMs = 10_000;

    public const string Sddl = "D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x2000D;;;AU)";

    public static string ImagePath(string installFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        return "\"" + Path.Combine(installFolder, TaskPlan.ExecutableName) + "\" " + RunModeArgument;
    }

    public static ServiceSpec Spec(string installFolder) =>
        new(
            ServiceName, DisplayName, Description, ImagePath(installFolder),
            AdvApi32.SERVICE_WIN32_OWN_PROCESS, AdvApi32.SERVICE_AUTO_START, AdvApi32.SERVICE_ERROR_NORMAL,
            AccountName, PreshutdownTimeoutMs, Sddl, DelayedAutoStart: false, ServiceSidType: ServiceSidTypeNone);

    // SERVICE_SID_TYPE_NONE.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_sid_info
    public const uint ServiceSidTypeNone = 0;
}

// Checks a service read back from the control manager against its spec and lists every difference. An empty list
// is the only pass, and a value that could not be read is a difference, so a service whose registration cannot be
// seen is never accepted.
internal static class ServiceCheck
{
    public static IReadOnlyList<string> Verify(ServiceQuery read, ServiceSpec expected)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(expected);
        var problems = new List<string>();
        if (read.Presence != ServicePresence.Present)
        {
            problems.Add(read.Presence == ServicePresence.Missing
                ? "The service is not registered."
                : "The service could not be read.");
            return problems;
        }

        Compare(problems, "service type", read.ServiceType, expected.ServiceType);
        Compare(problems, "start type", read.StartType, expected.StartType);
        Compare(problems, "error control", read.ErrorControl, expected.ErrorControl);
        Compare(problems, "pre-shutdown time-out", read.PreshutdownTimeoutMs, expected.PreshutdownTimeoutMs);

        // Exactly the quoted path, so an unquoted path with a space in it, which the control manager would search
        // for a shorter program name, is never accepted.
        Compare(problems, "image path", read.ImagePath, expected.ImagePath);
        Compare(problems, "account", read.Account, expected.Account);
        Compare(problems, "display name", read.DisplayName, expected.DisplayName);

        // Everything the plan leaves unset, read back: a registration with a failure command, a restart, a trigger, a
        // delayed start, a service security identifier or a cut-down token is not the one this plan describes.
        if (read.FailureActionCount is null)
        {
            problems.Add("The failure actions could not be read.");
        }
        else if (read.FailureActionCount.Value != 0)
        {
            problems.Add("The service has " + read.FailureActionCount.Value + " failure action(s), not none.");
        }

        if (!string.IsNullOrWhiteSpace(read.FailureCommand))
        {
            problems.Add("The service runs a command when it fails: " + read.FailureCommand + ".");
        }

        if (read.DelayedAutoStart is null)
        {
            problems.Add("The delayed start setting could not be read.");
        }
        else if (read.DelayedAutoStart.Value != expected.DelayedAutoStart)
        {
            problems.Add("The delayed start setting is " + (read.DelayedAutoStart.Value ? "on" : "off") + ", not " + (expected.DelayedAutoStart ? "on" : "off") + ".");
        }

        Compare(problems, "trigger count", read.TriggerCount, 0);
        Compare(problems, "service security identifier type", read.ServiceSidType, expected.ServiceSidType);
        if (read.RequiredPrivileges is null)
        {
            problems.Add("The required privileges could not be read.");
        }
        else if (read.RequiredPrivileges.Count != 0)
        {
            problems.Add("The service is limited to " + read.RequiredPrivileges.Count + " privilege(s): " + string.Join(", ", read.RequiredPrivileges) + ".");
        }

        if (read.Sddl is null)
        {
            problems.Add("The access list of the service could not be read.");
        }
        else
        {
            problems.AddRange(AclCheck.CheckService(read.Sddl));
        }

        return problems;
    }

    private static void Compare(List<string> problems, string what, uint? actual, uint expected)
    {
        if (actual is null)
        {
            problems.Add("The " + what + " could not be read.");
        }
        else if (actual.Value != expected)
        {
            problems.Add("The " + what + " is " + actual.Value + ", not " + expected + ".");
        }
    }

    private static void Compare(List<string> problems, string what, string? actual, string expected)
    {
        if (actual is null)
        {
            problems.Add("The " + what + " could not be read.");
        }
        else if (!string.Equals(actual.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The " + what + " is " + actual + ", not " + expected + ".");
        }
    }
}

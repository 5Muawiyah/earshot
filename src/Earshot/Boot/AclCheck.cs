using System.ComponentModel;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Earshot.Boot;

// Evaluates a security descriptor read back from a folder or a scheduled task and lists every problem.
// An empty list means the descriptor is safe; anything unexpected is a problem (fail closed).
//
// The rules, which keep every file and task an elevated Earshot run trusts out of reach of a standard user:
//   - the owner is SYSTEM or Administrators;
//   - no allow ACE for any other SID carries write, append, delete, WRITE_DAC or WRITE_OWNER (or the
//     generic rights that include them), inherited and inherit-only ACEs included, because the files and
//     tasks created inside inherit them;
//   - %ProgramData%\Earshot is protected and grants Users read and execute at most;
//   - the \Earshot task folder grants the user read at most;
//   - \Earshot\Gate and \Earshot\Protect grant the user 0x1200a9 and nothing more, including FILE_EXECUTE,
//     so the tray may start them; \Earshot\BootBlock grants the user 0x1200a9 at most.
// Deny ACEs only remove access, so they are not problems. A null DACL grants everyone everything.
//
// The descriptor is parsed with RawSecurityDescriptor (ConvertStringSecurityDescriptorToSecurityDescriptor),
// so aliases such as FA or BU and hex masks read the same.
// https://learn.microsoft.com/en-us/dotnet/api/system.security.accesscontrol.rawsecuritydescriptor
// https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights
// https://learn.microsoft.com/en-us/windows/win32/secauthz/access-mask
internal static class AclCheck
{
    private const uint FILE_WRITE_DATA = 0x00000002;
    private const uint FILE_APPEND_DATA = 0x00000004;
    private const uint FILE_WRITE_EA = 0x00000010;
    private const uint FILE_DELETE_CHILD = 0x00000040;
    private const uint FILE_WRITE_ATTRIBUTES = 0x00000100;
    private const uint DELETE = 0x00010000;
    private const uint WRITE_DAC = 0x00040000;
    private const uint WRITE_OWNER = 0x00080000;
    private const uint ACCESS_SYSTEM_SECURITY = 0x01000000;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint GENERIC_ALL = 0x10000000;
    private const uint GENERIC_EXECUTE = 0x20000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_READ = 0x80000000;

    // Generic rights mapped the way files map them (FILE_GENERIC_*, FILE_ALL_ACCESS). Task Scheduler uses the
    // same FA/FR/FX names for tasks.
    private const uint FILE_GENERIC_READ = 0x00120089;
    private const uint FILE_GENERIC_WRITE = 0x00120116;
    private const uint FILE_GENERIC_EXECUTE = 0x001200A0;
    private const uint FILE_ALL_ACCESS = 0x001F01FF;

    // Every bit that lets a holder change the object, its contents, its security or its owner.
    internal const uint WriteLikeRights =
        FILE_WRITE_DATA | FILE_APPEND_DATA | FILE_WRITE_EA | FILE_DELETE_CHILD | FILE_WRITE_ATTRIBUTES |
        DELETE | WRITE_DAC | WRITE_OWNER | ACCESS_SYSTEM_SECURITY | MAXIMUM_ALLOWED;

    private static readonly SecurityIdentifier LocalSystem = new(Sddl.LocalSystemSid);
    private static readonly SecurityIdentifier Administrators = new(Sddl.AdministratorsSid);
    private static readonly SecurityIdentifier Users = new(Sddl.UsersSid);
    private static readonly SecurityIdentifier CreatorOwner = new(Sddl.CreatorOwnerSid);
    private static readonly SecurityIdentifier TrustedInstaller = new(Sddl.TrustedInstallerSid);

    // %ProgramData%\Earshot.
    public static IReadOnlyList<string> CheckMachineFolder(string? sddl)
    {
        var problems = new List<string>();
        RawSecurityDescriptor? sd = Parse(sddl, problems);
        if (sd is null)
        {
            return problems;
        }

        CheckOwner(sd, problems, allowTrustedInstaller: false);
        if ((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
        {
            problems.Add("The DACL is not protected, so it inherits from its parent.");
        }

        CheckAces(sd, problems, allowTrustedInstaller: false, (sid, ace, mask) =>
        {
            if (sid == Users && (mask & ~Sddl.FileGenericReadExecute) == 0)
            {
                return null;
            }

            return "Unexpected access for " + sid.Value + ": " + Hex(mask) + ".";
        });

        return problems;
    }

    // %ProgramFiles%\Earshot after the copy. It keeps the Program Files inheritance: Users and the app
    // package groups read and execute, and CREATOR OWNER full control on inherit-only ACEs, which applies
    // only to objects created by someone who could already write there (an administrator).
    public static IReadOnlyList<string> CheckInstallFolder(string? sddl)
    {
        var problems = new List<string>();
        RawSecurityDescriptor? sd = Parse(sddl, problems);
        if (sd is null)
        {
            return problems;
        }

        CheckOwner(sd, problems, allowTrustedInstaller: true);
        CheckAces(sd, problems, allowTrustedInstaller: true, allowCreatorOwnerInheritOnly: true, rule: static (_, _, _) => null);
        return problems;
    }

    // The \Earshot task folder.
    public static IReadOnlyList<string> CheckTaskFolder(string? sddl, string userSid)
    {
        var problems = new List<string>();
        SecurityIdentifier? user = ParseUser(userSid, problems);
        RawSecurityDescriptor? sd = Parse(sddl, problems);
        if (sd is null || user is null)
        {
            return problems;
        }

        CheckOwner(sd, problems, allowTrustedInstaller: false);
        CheckAces(sd, problems, allowTrustedInstaller: false, (sid, ace, mask) =>
            sid == user && (mask & ~Sddl.FileGenericRead) == 0
                ? null
                : "Unexpected access for " + sid.Value + ": " + Hex(mask) + ".");
        return problems;
    }

    // \Earshot\Gate and \Earshot\Protect (userMayRun true), \Earshot\BootBlock (false).
    public static IReadOnlyList<string> CheckTask(string? sddl, string userSid, bool userMayRun)
    {
        var problems = new List<string>();
        SecurityIdentifier? user = ParseUser(userSid, problems);
        RawSecurityDescriptor? sd = Parse(sddl, problems);
        if (sd is null || user is null)
        {
            return problems;
        }

        CheckOwner(sd, problems, allowTrustedInstaller: false);
        uint userMask = 0;
        CheckAces(sd, problems, allowTrustedInstaller: false, (sid, ace, mask) =>
        {
            if (sid != user)
            {
                return "Unexpected access for " + sid.Value + ": " + Hex(mask) + ".";
            }

            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AceFlags & AceFlags.InheritOnly) == 0)
            {
                userMask |= mask;
            }

            return (mask & ~Sddl.FileGenericReadExecute) == 0
                ? null
                : "The user holds more than 0x001200A9: " + Hex(mask) + ".";
        });

        if (userMayRun && (userMask & Sddl.FileExecute) == 0)
        {
            problems.Add("The user " + user.Value + " cannot start the task (no FILE_EXECUTE).");
        }

        return problems;
    }

    // The access the user holds through allow ACEs that apply to the object itself, mapped. Used by the
    // probe to say whether the tray may start a task.
    public static uint UserAllowedMask(string? sddl, string userSid)
    {
        var ignored = new List<string>();
        RawSecurityDescriptor? sd = Parse(sddl, ignored);
        if (sd?.DiscretionaryAcl is null || !Sddl.IsUserSid(userSid))
        {
            return 0;
        }

        var user = new SecurityIdentifier(userSid);
        uint mask = 0;
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is QualifiedAce q && q.AceQualifier == AceQualifier.AccessAllowed &&
                (q.AceFlags & AceFlags.InheritOnly) == 0 && q.SecurityIdentifier == user)
            {
                mask |= MapGeneric(unchecked((uint)q.AccessMask));
            }
        }

        return mask;
    }

    internal static uint MapGeneric(uint mask)
    {
        uint mapped = mask & ~(GENERIC_ALL | GENERIC_EXECUTE | GENERIC_WRITE | GENERIC_READ);
        if ((mask & GENERIC_READ) != 0)
        {
            mapped |= FILE_GENERIC_READ;
        }

        if ((mask & GENERIC_WRITE) != 0)
        {
            mapped |= FILE_GENERIC_WRITE;
        }

        if ((mask & GENERIC_EXECUTE) != 0)
        {
            mapped |= FILE_GENERIC_EXECUTE;
        }

        if ((mask & GENERIC_ALL) != 0)
        {
            mapped |= FILE_ALL_ACCESS;
        }

        return mapped;
    }

    private static RawSecurityDescriptor? Parse(string? sddl, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(sddl))
        {
            problems.Add("No security descriptor was read.");
            return null;
        }

        try
        {
            return new RawSecurityDescriptor(sddl);
        }
        catch (ArgumentException ex)
        {
            problems.Add("The security descriptor does not parse: " + ex.Message);
        }
        catch (Win32Exception ex)
        {
            problems.Add("The security descriptor does not parse: " + ex.Message);
        }

        return null;
    }

    private static SecurityIdentifier? ParseUser(string userSid, List<string> problems)
    {
        if (!Sddl.IsUserSid(userSid))
        {
            problems.Add("Not a user SID: " + userSid);
            return null;
        }

        return new SecurityIdentifier(userSid);
    }

    private static void CheckOwner(RawSecurityDescriptor sd, List<string> problems, bool allowTrustedInstaller)
    {
        SecurityIdentifier? owner = sd.Owner;
        if (owner is null)
        {
            problems.Add("The owner was not read.");
        }
        else if (!IsPrivileged(owner, allowTrustedInstaller))
        {
            problems.Add("The owner is " + owner.Value + ", not SYSTEM or Administrators.");
        }
    }

    private static void CheckAces(
        RawSecurityDescriptor sd,
        List<string> problems,
        bool allowTrustedInstaller,
        Func<SecurityIdentifier, QualifiedAce, uint, string?> rule) =>
        CheckAces(sd, problems, allowTrustedInstaller, allowCreatorOwnerInheritOnly: false, rule);

    // Applies the shared rules to every ACE, then the caller's rule to allow ACEs for other SIDs.
    private static void CheckAces(
        RawSecurityDescriptor sd,
        List<string> problems,
        bool allowTrustedInstaller,
        bool allowCreatorOwnerInheritOnly,
        Func<SecurityIdentifier, QualifiedAce, uint, string?> rule)
    {
        RawAcl? dacl = sd.DiscretionaryAcl;
        if (dacl is null)
        {
            problems.Add("There is no DACL, which grants everyone full access.");
            return;
        }

        foreach (GenericAce ace in dacl)
        {
            if (ace is not QualifiedAce qualified)
            {
                problems.Add("Unsupported ACE type " + ace.AceType + ".");
                continue;
            }

            if (qualified.AceQualifier is AceQualifier.AccessDenied or AceQualifier.SystemAudit or AceQualifier.SystemAlarm)
            {
                continue;
            }

            if (qualified.IsCallback)
            {
                problems.Add("Conditional ACE for " + qualified.SecurityIdentifier.Value + ".");
                continue;
            }

            SecurityIdentifier sid = qualified.SecurityIdentifier;
            if (IsPrivileged(sid, allowTrustedInstaller))
            {
                continue;
            }

            uint mask = MapGeneric(unchecked((uint)qualified.AccessMask));
            if (allowCreatorOwnerInheritOnly && sid == CreatorOwner && (qualified.AceFlags & AceFlags.InheritOnly) != 0)
            {
                continue;
            }

            if ((mask & WriteLikeRights) != 0)
            {
                problems.Add(sid.Value + " can write, delete or change security: " + Hex(mask) + ".");
                continue;
            }

            string? problem = rule(sid, qualified, mask);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }
    }

    private static bool IsPrivileged(SecurityIdentifier sid, bool allowTrustedInstaller) =>
        sid == LocalSystem || sid == Administrators || (allowTrustedInstaller && sid == TrustedInstaller);

    private static string Hex(uint mask) => "0x" + mask.ToString("X8", CultureInfo.InvariantCulture);
}

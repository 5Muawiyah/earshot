using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Earshot.Setup;

internal readonly record struct AclStep(bool Ok, int Code, string Detail);

// Creating and reading folder security, behind an interface so the staging rules are tested without an elevated token.
internal interface IFolderAcl
{
    // Creates the folder (and a missing parent) with this descriptor. With failIfExists, a folder or file already there is a
    // failure: the caller never gets a folder somebody else made first.
    AclStep CreateWithSddl(string path, string sddl, bool failIfExists);

    // The folder's owner, group and DACL as SDDL. Fails for a missing folder and for a reparse point.
    AclStep ReadSddl(string path, out string? sddl);

    bool Exists(string path);
}

// https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemaclextensions.create
// https://learn.microsoft.com/en-us/dotnet/api/system.security.accesscontrol.objectsecurity.getsecuritydescriptorsddlform
internal sealed class NtfsFolderAcl : IFolderAcl
{
    private const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    public AclStep CreateWithSddl(string path, string sddl, bool failIfExists)
    {
        try
        {
            if (failIfExists && Exists(path))
            {
                return new AclStep(false, NotAttempted, "Already exists, so it was not trusted: " + path);
            }

            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(sddl, Sections);
            new DirectoryInfo(path).Create(security);
            return new AclStep(true, 0, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return new AclStep(false, ex.HResult, path + ": " + ex.Message);
        }
    }

    public AclStep ReadSddl(string path, out string? sddl)
    {
        sddl = null;
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return new AclStep(false, unchecked((int)0x80070003), "Missing: " + path);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new AclStep(false, NotAttempted, "The folder is a reparse point: " + path);
            }

            sddl = info.GetAccessControl(Sections).GetSecurityDescriptorSddlForm(Sections);
            return new AclStep(true, 0, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return new AclStep(false, ex.HResult, path + ": " + ex.Message);
        }
    }

    // The code for a step refused on purpose, with no call made.
    public const int NotAttempted = unchecked((int)0xE0000001);
}

// What a folder Setup runs a program from must look like, read back as SDDL after it is made.
internal static class StagingAcl
{
    // %ProgramData%\Earshot as install makes it: protected, SYSTEM and Administrators full control, Users read and execute.
    public const string MachineFolderSddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    // The staging folders: protected, owner Administrators, SYSTEM and Administrators full control and nobody else.
    public const string AdminOnlySddl = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";

    // Every right that lets a holder change a folder, what is in it, its security or its owner. Generic rights are listed
    // because a descriptor may carry them unmapped.
    private const uint WriteLike =
        0x00000002 | 0x00000004 | 0x00000010 | 0x00000040 | 0x00000100 | 0x00010000 | 0x00040000 | 0x00080000 |
        0x01000000 | 0x02000000 | 0x10000000 | 0x40000000;

    // Problems with a descriptor that must be only administrators', or an empty list. strict: nobody but SYSTEM and
    // Administrators may be allowed anything at all (the folders that hold the program Setup runs); otherwise other accounts
    // may read and execute but not change (the Earshot folder in ProgramData that holds them).
    public static IReadOnlyList<string> Check(string? sddl, bool strict)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(sddl))
        {
            problems.Add("The security could not be read.");
            return problems;
        }

        RawSecurityDescriptor descriptor;
        try
        {
            descriptor = new RawSecurityDescriptor(sddl);
        }
        catch (ArgumentException ex)
        {
            problems.Add("The security descriptor could not be parsed: " + ex.Message);
            return problems;
        }

        var system = new SecurityIdentifier(SystemSid);
        var administrators = new SecurityIdentifier(AdministratorsSid);
        if (descriptor.Owner is null || (descriptor.Owner != system && descriptor.Owner != administrators))
        {
            problems.Add("The owner is " + (descriptor.Owner?.Value ?? "missing") + ", not SYSTEM or Administrators.");
        }

        if (descriptor.DiscretionaryAcl is null)
        {
            problems.Add("There is no DACL, which grants everyone full access.");
            return problems;
        }

        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
        {
            problems.Add("The DACL is not protected, so it takes permissions from the folder above.");
        }

        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce { AceQualifier: AceQualifier.AccessAllowed } allowed)
            {
                continue;
            }

            if (allowed.SecurityIdentifier == system || allowed.SecurityIdentifier == administrators)
            {
                continue;
            }

            if (strict)
            {
                problems.Add("Access is allowed for " + allowed.SecurityIdentifier.Value + ", not only SYSTEM and Administrators.");
            }
            else if ((unchecked((uint)allowed.AccessMask) & WriteLike) != 0)
            {
                problems.Add("Access that changes the folder is allowed for " + allowed.SecurityIdentifier.Value + " (0x" +
                             unchecked((uint)allowed.AccessMask).ToString("X8", CultureInfo.InvariantCulture) + ").");
            }
        }

        return problems;
    }
}

internal sealed record StagingResult(bool Ok, string Path, string Detail);

// The folder Setup unpacks into: %ProgramData%\Earshot\setup-staging\<guid>, made by this elevated process with a protected
// DACL that only SYSTEM and Administrators are in, and read back before anything is put in it. A standard user can create
// folders in %ProgramData% and owns what they create, so a folder that already exists on the way is trusted only when its
// security reads back as it should; one that does not is refused and left for the person to remove, never deleted from
// here.
internal sealed class StagingFolder
{
    public const string FolderName = "setup-staging";

    private readonly IFolderAcl _acl;
    private readonly string _machineFolder;
    private readonly Func<string> _newName;

    public StagingFolder(IFolderAcl acl, string machineFolder, Func<string>? newName = null)
    {
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineFolder);
        _acl = acl;
        _machineFolder = machineFolder;
        _newName = newName ?? (() => Guid.NewGuid().ToString("N"));
    }

    public StagingResult Create()
    {
        string root = _machineFolder;
        string staging = Path.Combine(root, FolderName);
        StagingResult? refusal = Ensure(root, StagingAcl.MachineFolderSddl, strict: false)
                                 ?? Ensure(staging, StagingAcl.AdminOnlySddl, strict: true);
        if (refusal is not null)
        {
            return refusal;
        }

        string work = Path.Combine(staging, _newName());
        AclStep created = _acl.CreateWithSddl(work, StagingAcl.AdminOnlySddl, failIfExists: true);
        if (!created.Ok)
        {
            return Refused("The working folder could not be made. " + Describe(created));
        }

        return Verify(work, strict: true) ?? new StagingResult(true, work, "");
    }

    // Makes the folder when it is not there, and requires the security it reads back to pass. Null when it is fine.
    private StagingResult? Ensure(string path, string sddl, bool strict)
    {
        if (!_acl.Exists(path))
        {
            AclStep created = _acl.CreateWithSddl(path, sddl, failIfExists: true);
            if (!created.Ok)
            {
                return Refused("A folder for Setup could not be made. " + Describe(created));
            }
        }

        return Verify(path, strict);
    }

    private StagingResult? Verify(string path, bool strict)
    {
        AclStep read = _acl.ReadSddl(path, out string? sddl);
        if (!read.Ok)
        {
            return Refused("The folder's security could not be read, so it was not used. " + Describe(read));
        }

        IReadOnlyList<string> problems = StagingAcl.Check(sddl, strict);
        return problems.Count == 0
            ? null
            : Refused(path + " is not safe to run a program from: " + string.Join(" ", problems) + " Remove it by hand and run Setup again.");
    }

    private static StagingResult Refused(string detail) => new(false, "", detail);

    private static string Describe(AclStep step) =>
        "0x" + step.Code.ToString("X8", CultureInfo.InvariantCulture) + ": " + step.Detail;
}

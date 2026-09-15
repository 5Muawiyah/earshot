using System.Globalization;

namespace Earshot.Boot;

// The security descriptors setup applies, as SDDL strings. One source for install (which applies them)
// and for the read-back checks (AclCheck), so the two cannot drift.
//
//   TaskFolder      \Earshot task folder: SYSTEM and Administrators full control (inherited by the tasks),
//                   the interactive user read, so the tray can open the folder and its tasks.
//   RunnableTask    \Earshot\Gate and \Earshot\Protect: SYSTEM and Administrators full control, the
//                   interactive user FRFX (0x1200a9), the mask Microsoft's SilentCleanup task grants so a
//                   standard user can start an elevated task. FR (0x120089) alone lacks FILE_EXECUTE.
//   ReadableTask    \Earshot\BootBlock: as above, but the user may only read it. The boot trigger starts
//                   it; the tray never does.
//   MachineFolder   %ProgramData%\Earshot: protected (no inheritance from ProgramData, which grants Users
//                   write and append), SYSTEM and Administrators full control, Users read and execute,
//                   owner Administrators.
//
// https://learn.microsoft.com/en-us/windows/win32/secauthz/security-descriptor-string-format
// https://learn.microsoft.com/en-us/windows/win32/secauthz/ace-strings
// https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks
internal static class Sddl
{
    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";
    public const string CreatorOwnerSid = "S-1-3-0";

    // NT SERVICE\TrustedInstaller, which owns and fully controls parts of %ProgramFiles%.
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // FILE_GENERIC_READ (0x120089): READ_CONTROL | SYNCHRONIZE | FILE_READ_DATA | FILE_READ_EA | FILE_READ_ATTRIBUTES.
    public const uint FileGenericRead = 0x00120089;

    // FILE_GENERIC_READ | FILE_GENERIC_EXECUTE (FRFX): adds FILE_EXECUTE (0x20).
    public const uint FileGenericReadExecute = 0x001200A9;

    public const uint FileExecute = 0x00000020;

    public const string MachineFolder = "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    public static string TaskFolder(string userSid) =>
        "O:BAG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;FR;;;" + RequireUserSid(userSid) + ")";

    public static string RunnableTask(string userSid) =>
        "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;" + RequireUserSid(userSid) + ")";

    public static string ReadableTask(string userSid) =>
        "O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;" + RequireUserSid(userSid) + ")";

    // A local or Microsoft account user (S-1-5-21-a-b-c-rid) or a Microsoft Entra user (S-1-12-1-a-b-c-d),
    // with every sub-authority a 32-bit value. Only digits and hyphens, so the value can never change the
    // meaning of an SDDL string it is placed in, and well-known SIDs such as SYSTEM, Everyone or
    // Authenticated Users never pass.
    // https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/understand-security-identifiers
    public static bool IsUserSid(string? sid)
    {
        if (sid is null || sid.Length > 80)
        {
            return false;
        }

        string rest;
        if (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
        {
            rest = sid["S-1-5-21-".Length..];
        }
        else if (sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
        {
            rest = sid["S-1-12-1-".Length..];
        }
        else
        {
            return false;
        }

        string[] parts = rest.Split('-');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length is 0 or > 10 || !part.All(char.IsAsciiDigit) ||
                (part.Length > 1 && part[0] == '0') ||
                !uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static string RequireUserSid(string userSid)
    {
        if (!IsUserSid(userSid))
        {
            throw new ArgumentException("Not a user SID: " + userSid, nameof(userSid));
        }

        return userSid;
    }
}

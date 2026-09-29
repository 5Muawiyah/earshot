using System.Globalization;
using System.Reflection;

namespace Earshot.Update;

// A release version as the release tags write it ("v1.2.0"), compared number by number so 1.10.0 is newer than
// 1.9.0. Three or four numbers; a tag that carries anything else (a suffix such as -beta, a build tag, a missing
// number, a sign) is not a version this reads, so a check refuses it rather than guess what it means.
internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch, int Revision = 0) : IComparable<ReleaseVersion>
{
    private const int MaxTextLength = 40;
    private const int MaxDigits = 9;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
        {
            return false;
        }

        string body = text[0] is 'v' or 'V' ? text[1..] : text;
        string[] parts = body.Split('.');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        int[] numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length is 0 or > MaxDigits || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    // The version the running program reports: its informational version (the one place the release version is
    // written), without any build metadata after a "+". Null when the program does not report a readable one.
    public static ReleaseVersion? Running(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (text is null)
        {
            return null;
        }

        int plus = text.IndexOf('+', StringComparison.Ordinal);
        return TryParse(plus >= 0 ? text[..plus] : text, out ReleaseVersion parsed) ? parsed : null;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : Revision.CompareTo(other.Revision);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    // "1.2.0", with the fourth number only when it is not zero.
    public override string ToString() =>
        Revision == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Revision}");
}

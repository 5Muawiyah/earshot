namespace Earshot.Infra;

// Whether two paths name the same file, as Windows compares them: the full path of each, without regard to case, so a path
// with a ".." in it or forward slashes is the same file as its plain spelling. A path that cannot be made a full path (a
// character Windows does not allow, or one that is too long) is compared as written, which is the most that can be said of
// it. A blank path names nothing, so it is never the same as another. One rule for every place that asks "is this running
// copy the installed one", so two of them cannot disagree about it.
internal static class SamePath
{
    public static bool AreEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path Windows can name in full: compared as written.
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

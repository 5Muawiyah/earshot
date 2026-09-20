namespace Earshot.Tests.TestWindow;

// Shared by the TestWindow test fixtures: where the repository root is, found the same way
// Program.cs and the other test suites do (walking up from the running assembly for
// Earshot.slnx), so a test never hard-codes a path that only matches one machine.
internal static class RepositoryLocator
{
    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}

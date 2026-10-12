using System.Text.RegularExpressions;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// The mode table in Program.cs says what each mode does. A mode whose hook has no body in this build must not be described as
// working. The test reads the source, never dispatches the mode: running fix would do the real thing once it exists.
[TestClass]
public sealed class ProgramHeaderCommentTests
{
    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), relative));

    private static bool HookHasABody(string hook)
    {
        string pattern = @"static\s+partial\s+void\s+" + hook + @"\s*\(\s*RunContext\s+ctx\s*\)\s*\r?\n\s*\{";
        string srcFolder = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot");
        return Directory.EnumerateFiles(srcFolder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Any(f => Regex.IsMatch(File.ReadAllText(f), pattern));
    }

    [TestMethod]
    [DataRow("fix", "TryRunFix")]
    [DataRow("fix-apply", "TryRunFixApply")]
    [DataRow("doctor", "TryRunDoctor")]     // implemented: proves the body is found
    [DataRow("settings", "TryRunSettings")] // implemented: proves the body is found
    public void AModeWithNoHookBodyIsDescribedAsNotImplemented(string mode, string hook)
    {
        string table = Source(Path.Combine("src", "Earshot", "Program.cs"));
        string line = table.Split('\n').Single(l => l.StartsWith("//   " + mode + " ", StringComparison.Ordinal) && l.Contains(hook, StringComparison.Ordinal));

        bool implemented = HookHasABody(hook);

        Assert.AreEqual(!implemented, line.Contains("not implemented in this build", StringComparison.Ordinal),
            hook + (implemented ? " has a body, so the table must not call it not implemented: " : " has no body, so the table must say so: ") + line.Trim());
    }
}

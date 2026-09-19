using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// Acceptance test 16. No diagnostic is hidden anywhere in the solution: not by the compiler directive, not by the
// attribute, not by the project property, and not by an analyser severity override. tools\check.ps1 scans for the
// same things and more; this is the copy that runs with the tests, so a later shortcut fails the suite as well.
//
// This file has to name what it bans, and it scans itself like every other file, so the words never appear here as
// literals: each one is put together from fragments when the test runs. No file is skipped, this one included. The
// only folders left out are bin and obj, which hold what the build wrote and not what anyone typed.
[TestClass]
public sealed class NoSuppressionsTests
{
    private static readonly string[] Extensions = [".cs", ".csproj", ".props", ".targets", ".editorconfig", ".globalconfig", ".ruleset"];

    [TestMethod]
    public void NoSuppressionsAnywhere()
    {
        string root = SolutionRoot();

        var needles = new (string What, Regex Pattern)[]
        {
            ("the compiler directive", new Regex("#\\s*" + "pragma" + "\\s+" + "warning" + "\\s+" + "disable", RegexOptions.CultureInvariant)),
            ("the attribute", new Regex("Suppress" + "Message", RegexOptions.CultureInvariant)),
            ("the project property", new Regex("<\\s*" + "No" + "Warn" + "\\b", RegexOptions.CultureInvariant)),
            ("warnings taken back out of errors", new Regex("Warnings" + "NotAs" + "Errors", RegexOptions.CultureInvariant)),
            ("an analyser severity override", new Regex("dotnet_" + "(analyzer_)?" + "diagnostic" + "\\.[^=\\r\\n]*=\\s*(none|silent|suggestion)", RegexOptions.CultureInvariant)),
            ("a severity set to none", new Regex("severity" + "\\s*=\\s*" + "none", RegexOptions.CultureInvariant)),
        };

        var files = new List<string>();
        foreach (string folder in Sequence.Of("src", "tests"))
        {
            files.AddRange(Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories).Where(IsSource));
        }

        files.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Where(IsSource));

        var found = new List<string>();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);

            // A comment in an MSBuild file is prose (Directory.Build.props states this very rule in one) and hides
            // nothing, so it is blanked, as the gate script does. Code files are read exactly as they are.
            if (Path.GetExtension(file) is ".csproj" or ".props" or ".targets" or ".ruleset")
            {
                text = Regex.Replace(text, "<!--[\\s\\S]*?-->", "", RegexOptions.CultureInvariant);
            }

            foreach ((string what, Regex pattern) in needles)
            {
                if (pattern.IsMatch(text))
                {
                    found.Add(Path.GetRelativePath(root, file) + ": " + what);
                }
            }
        }

        Assert.IsTrue(files.Count >= 8, "Only " + files.Count + " files were read under " + root + ", so a clean result would prove nothing.");
        Assert.IsTrue(files.Any(f => f.EndsWith("NoSuppressionsTests.cs", StringComparison.Ordinal)), "This file must be among the files it reads.");
        Assert.IsTrue(files.Any(f => f.EndsWith("WindowsStreamingPlatform.cs", StringComparison.Ordinal)), "The one file that calls WinRT must be among them.");
        Assert.IsTrue(files.Any(f => Path.GetFileName(f) == "Directory.Build.props"), "The file that sets the supported Windows version must be among them.");
        Assert.AreEqual(0, found.Count, "A diagnostic is being hidden:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    // The guard on the WinRT calls is only real while the supported Windows version stays below the version that
    // introduced them. Raising it would silence CA1416 as surely as any of the four above, and no scan for words
    // would notice, so the number itself is pinned here.
    [TestMethod]
    public void TheSupportedWindowsVersionStaysBelowTheOneTheWinRtCallsNeed()
    {
        string props = File.ReadAllText(Path.Combine(SolutionRoot(), "Directory.Build.props"));
        props = Regex.Replace(props, "<!--[\\s\\S]*?-->", "", RegexOptions.CultureInvariant);

        Match framework = Regex.Match(props, "<TargetFramework>([^<]+)</TargetFramework>", RegexOptions.CultureInvariant);
        Match supported = Regex.Match(props, "<SupportedOSPlatformVersion>([^<]+)</SupportedOSPlatformVersion>", RegexOptions.CultureInvariant);

        Assert.IsTrue(framework.Success, "Directory.Build.props sets no TargetFramework.");
        Assert.AreEqual("net10.0-windows10.0.19041.0", framework.Groups[1].Value.Trim());
        Assert.IsTrue(supported.Success, "SupportedOSPlatformVersion must be set on purpose: left out, it silently becomes the version in the moniker.");
        Assert.IsTrue(Version.Parse(supported.Groups[1].Value.Trim()) < new Version(10, 0, 19041, 0),
            "At or above 10.0.19041.0 the analyser stops asking for the guard on every WinRT call.");

        // What the build actually stamped on this test assembly, which takes the same two values from the same file.
        var attribute = (System.Runtime.Versioning.SupportedOSPlatformAttribute?)Attribute.GetCustomAttribute(
            typeof(NoSuppressionsTests).Assembly, typeof(System.Runtime.Versioning.SupportedOSPlatformAttribute));
        Assert.IsNotNull(attribute);
        Assert.AreEqual("Windows7.0", attribute.PlatformName);
    }

    private static bool IsSource(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        if (path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string name = Path.GetFileName(path);
        return Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static string SolutionRoot()
    {
        string start = AppContext.BaseDirectory;
        for (DirectoryInfo? folder = new(start); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, "src")) && Directory.Exists(Path.Combine(folder.FullName, "tests")))
            {
                return folder.FullName;
            }
        }

        throw new AssertFailedException("No folder holding both src and tests was found above " + start + ".");
    }
}

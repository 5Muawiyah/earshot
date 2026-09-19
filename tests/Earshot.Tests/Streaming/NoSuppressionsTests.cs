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
    // introduced them. Raising it silences CA1416 as surely as any of the words above, and no scan for words notices.
    // So what ships is read: the two platform attributes on the built Earshot.dll this test project references, not
    // the ones on the test assembly, which says nothing about a project file that sets its own value.
    [TestMethod]
    public void TheBuiltApplicationStillDeclaresTheFloorBelowTheOneTheWinRtCallsNeed()
    {
        System.Reflection.Assembly application = typeof(Earshot.Streaming.StreamingCoordinator).Assembly;
        Assert.AreEqual("Earshot", application.GetName().Name);
        Assert.AreNotSame(typeof(NoSuppressionsTests).Assembly, application);
        StringAssert.EndsWith(application.Location, "Earshot.dll", StringComparison.OrdinalIgnoreCase);

        string[] supported = PlatformAttribute(application, "System.Runtime.Versioning.SupportedOSPlatformAttribute");
        string[] target = PlatformAttribute(application, "System.Runtime.Versioning.TargetPlatformAttribute");

        CollectionAssert.AreEqual(Sequence.Of("Windows7.0"), supported,
            "Earshot.dll declares SupportedOSPlatform(" + string.Join(", ", supported) + "). At or above Windows10.0.19041.0 the analyser stops asking for the guard on every WinRT call.");
        CollectionAssert.AreEqual(Sequence.Of("Windows10.0.19041.0"), target);
    }

    // The floor is written in one place. A second SupportedOSPlatformVersion in any project, props or targets file
    // overrides Directory.Build.props for that project, and an assembly-level attribute in code does the same from
    // inside; either one takes the guard off without a word the scan above would catch.
    [TestMethod]
    public void TheFloorIsSetInDirectoryBuildPropsAndNowhereElse()
    {
        string root = SolutionRoot();
        var projectFiles = new List<string>();
        var codeFiles = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith('.') || !IsSource(file) ||
                relative.StartsWith("publish" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (Path.GetExtension(file) == ".cs" ? codeFiles : projectFiles).Add(file);
        }

        string floor = "Supported" + "OSPlatform";
        var set = new List<string>();
        foreach (string file in projectFiles.Where(f => Path.GetExtension(f) is ".csproj" or ".props" or ".targets"))
        {
            string text = Regex.Replace(File.ReadAllText(file), "<!--[\\s\\S]*?-->", "", RegexOptions.CultureInvariant);
            foreach (Match match in Regex.Matches(text, floor + "[A-Za-z]*", RegexOptions.CultureInvariant))
            {
                set.Add(Path.GetRelativePath(root, file) + ": " + match.Value);
            }
        }

        CollectionAssert.AreEqual(Sequence.Of("Directory.Build.props: " + floor + "Version", "Directory.Build.props: " + floor + "Version"), set,
            "The floor must be set exactly once, in Directory.Build.props (an opening and a closing tag). Found: " + string.Join("; ", set));

        string props = Regex.Replace(File.ReadAllText(Path.Combine(root, "Directory.Build.props")), "<!--[\\s\\S]*?-->", "", RegexOptions.CultureInvariant);
        Match framework = Regex.Match(props, "<TargetFramework>([^<]+)</TargetFramework>", RegexOptions.CultureInvariant);
        Match supported = Regex.Match(props, "<" + floor + "Version>([^<]+)</" + floor + "Version>", RegexOptions.CultureInvariant);
        Assert.AreEqual("net10.0-windows10.0.19041.0", framework.Groups[1].Value.Trim());
        Assert.IsTrue(supported.Success);
        Assert.IsTrue(Version.Parse(supported.Groups[1].Value.Trim()) < new Version(10, 0, 19041, 0));

        var inCode = new Regex("\\[\\s*assembly\\s*:[^\\]]*" + floor, RegexOptions.CultureInvariant);
        string[] assemblyLevel = codeFiles.Where(f => inCode.IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)).ToArray();
        Assert.AreEqual(0, assemblyLevel.Length, "An assembly-level platform attribute is written in code: " + string.Join(", ", assemblyLevel));
        Assert.IsTrue(projectFiles.Count >= 3 && codeFiles.Count >= 100, "Too few files were read for a clean result to mean anything.");
    }

    // Read as data, so nothing is constructed and the value is exactly what the compiler wrote into the file.
    private static string[] PlatformAttribute(System.Reflection.Assembly assembly, string attributeType) =>
        assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == attributeType)
            .Select(a => (string)a.ConstructorArguments[0].Value!)
            .ToArray();

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

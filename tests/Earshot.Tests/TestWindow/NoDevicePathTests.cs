using System.Reflection;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// "Cannot touch the device" is a property this reads off the built output, not a claim about
// what the source is meant to do. The window references no product assembly, declares no
// P/Invoke, and the only file allowed to start a process is ChildRunner.cs.
[TestClass]
public sealed class NoDevicePathTests
{
    [TestMethod]
    public void TheTestWindowAssemblyReferencesNoAssemblyNamedEarshot()
    {
        Assembly testWindow = typeof(StartupGate).Assembly;
        Assert.AreEqual("Earshot.TestWindow", testWindow.GetName().Name);

        AssemblyName[] referenced = testWindow.GetReferencedAssemblies();
        Assert.IsFalse(
            referenced.Any(a => a.Name == "Earshot"),
            "Earshot.TestWindow references the product assembly: " +
            string.Join(", ", referenced.Select(a => a.Name)));
    }

    [TestMethod]
    public void NoMethodInTheTestWindowAssemblyIsAPInvoke()
    {
        Assembly testWindow = typeof(StartupGate).Assembly;
        var pinvokes = new List<string>();
        foreach (Type type in testWindow.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    pinvokes.Add(type.FullName + "." + method.Name);
                }
            }
        }

        Assert.AreEqual(0, pinvokes.Count, "P/Invoke found: " + string.Join(", ", pinvokes));
    }

    // "new Process(" and "Process.Start(" only: a plain word match on "Process" would also catch
    // ProcessStartInfo, which every child-starting file needs to configure the child and is not
    // itself a process start.
    private static readonly System.Text.RegularExpressions.Regex StartsAProcess =
        new(@"new\s+Process\s*\(|\bProcess\.Start\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    [TestMethod]
    public void ProcessIsStartedOnlyFromChildRunner()
    {
        string sourceRoot = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name is "ChildRunner.cs")
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (StartsAProcess.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(sourceRoot, file));
            }
        }

        Assert.AreEqual(0, offenders.Count, "A process is started outside ChildRunner.cs: " + string.Join(", ", offenders));
    }

    [TestMethod]
    public void TheProjectFileMarksItselfNotPublishable()
    {
        string projectFile = Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Earshot.TestWindow.csproj");
        string text = File.ReadAllText(projectFile);
        StringAssert.Contains(text, "<IsPublishable>false</IsPublishable>");
    }

    // The product's own build output must never name the test window: it has no ProjectReference
    // to it, and this is what proves that from what dotnet actually wrote, not from reading the
    // project file and trusting it.
    [TestMethod]
    public void TheProductsBuiltDepsJsonDoesNotNameTheTestWindow()
    {
        Assembly product = typeof(Earshot.Streaming.StreamingCoordinator).Assembly;
        string depsPath = Path.ChangeExtension(product.Location, ".deps.json");
        if (!File.Exists(depsPath))
        {
            Assert.Inconclusive("No " + depsPath + " was found, so this build was not checked.");
        }

        string text = File.ReadAllText(depsPath);
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("Earshot\\.TestWindow"));
    }
}

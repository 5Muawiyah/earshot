using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// A trace holds device addresses and may hold pairing keys. These are the rules that keep it on the PC.
[TestClass]
public sealed class TraceHygieneTests
{
    private static string? RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Earshot.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }

    private static IEnumerable<string> TraceSources()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            Assert.Inconclusive("The source tree is not beside the test assembly.");
        }

        return Directory.GetFiles(Path.Combine(root, "src", "Earshot", "Trace"), "*.cs")
            .Append(Path.Combine(root, "src", "Earshot", "App", "Program.Trace.cs"));
    }

    [TestMethod]
    public void NothingInTheTraceCodeTouchesTheNetwork()
    {
        string[] forbidden = ["HttpClient", "WebRequest", "WebClient", "System.Net", "Socket", "TcpClient", "SmtpClient", "InternetOpen", "WinHttp", "wininet"];
        foreach (string file in TraceSources())
        {
            string text = File.ReadAllText(file);
            foreach (string word in forbidden)
            {
                Assert.IsFalse(text.Contains(word, StringComparison.OrdinalIgnoreCase), Path.GetFileName(file) + " mentions " + word);
            }
        }
    }

    [TestMethod]
    public void TheTraceFolderHoldsTheSourceFilesThisTestReads()
    {
        Assert.IsTrue(TraceSources().Count() >= 6);
    }

    [TestMethod]
    public void TheNoticesCarryTheBusiotoolsLicence()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            Assert.Inconclusive("The source tree is not beside the test assembly.");
        }

        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));

        StringAssert.Contains(notices, "BluetoothStack.wprp");
        StringAssert.Contains(notices, "https://github.com/microsoft/busiotools");
        StringAssert.Contains(notices, "Copyright (c) Microsoft Corporation");
        StringAssert.Contains(notices, "Permission is hereby granted, free of charge");
        StringAssert.Contains(notices, "7f68a48cd21306c6b0519728ec311a0f66479aff");
        Assert.IsFalse(notices.Contains((char)0x2014), "An em-dash.");
    }

    [TestMethod]
    public void TheProfileIsEmbeddedByTheProject()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            Assert.Inconclusive("The source tree is not beside the test assembly.");
        }

        string project = File.ReadAllText(Path.Combine(root, "src", "Earshot", "Earshot.csproj"));
        StringAssert.Contains(project, "EmbeddedResource Include=\"Trace\\BluetoothStack.wprp\"");
        StringAssert.Contains(project, "Earshot.Trace.BluetoothStack.wprp");
    }
}

using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Infra;

[TestClass]
public sealed class FileLogTests
{
    [TestMethod]
    public void WritesUtcIsoTimestampLevelAndMessage()
    {
        using var temp = new TempFolder();
        var log = new FileLog(Path.Combine(temp.Path, "logs"));

        log.Info("Hello");
        log.Write(LogLevel.Debug, "Detail");

        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.HasCount(2, lines);
        Assert.MatchesRegex(new Regex(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z INFO Hello\z"), lines[0]);
        Assert.MatchesRegex(new Regex(@"\A\S+Z DEBUG Detail\z"), lines[1]);
    }

    [TestMethod]
    public void ExceptionTextIsIndentedUnderItsEntry()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path);

        log.Error("Failed", new InvalidOperationException("boom"));

        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.IsTrue(lines[0].EndsWith("ERROR Failed", StringComparison.Ordinal));
        Assert.IsTrue(lines[1].StartsWith("  System.InvalidOperationException: boom", StringComparison.Ordinal));
        Assert.IsTrue(lines.Skip(1).All(l => l.StartsWith("  ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RollsOverAtTheSizeCap()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path, maxBytes: 4096);
        string line = new('x', 200);

        for (int i = 0; i < 60; i++)
        {
            log.Info(line);
        }

        Assert.IsTrue(File.Exists(log.RolledFilePath));
        Assert.IsLessThanOrEqualTo(4096L, new FileInfo(log.FilePath).Length);
        Assert.IsLessThanOrEqualTo(4096L, new FileInfo(log.RolledFilePath).Length);
    }

    // An elevated run logging under a user's profile never renames or replaces a file there.
    [TestMethod]
    public void ALogThatDoesNotRollOnlyAppends()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path, maxBytes: 4096, rolls: false);
        string line = new('x', 200);

        for (int i = 0; i < 60; i++)
        {
            log.Info(line);
        }

        Assert.IsFalse(File.Exists(log.RolledFilePath));
        Assert.IsGreaterThan(4096L, new FileInfo(log.FilePath).Length);
        Assert.AreEqual(0, log.FailedWrites);
    }

    [TestMethod]
    public void WriteNeverThrowsAndReportsTheFailureLater()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path);
        log.Info("First");

        using (new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            log.Info("Lost");
            Assert.AreEqual(1, log.FailedWrites);
            Assert.IsNotNull(log.LastWriteError);
        }

        log.Info("Back");

        string text = File.ReadAllText(log.FilePath);
        Assert.AreEqual(0, log.FailedWrites);
        Assert.IsTrue(text.Contains("1 earlier log writes failed", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("INFO Back", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ConcurrentWritesAllLand()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path);

        Parallel.For(0, 200, i => log.Info("entry " + i));

        Assert.HasCount(200, File.ReadAllLines(log.FilePath));
    }
}

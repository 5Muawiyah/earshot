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
    public void AnEntryWrittenLaterKeepsTheTimeItWasMade()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path);

        log.WriteAt(new DateTime(2026, 9, 15, 20, 30, 1, 250, DateTimeKind.Utc), LogLevel.Info, "Held");

        Assert.AreEqual("2026-09-15T20:30:01.250Z INFO Held", File.ReadAllLines(log.FilePath).Single());
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
    // The log of an install run must survive a day of logging after it: one backup of 1 MB was rolled away within hours, which is
    // how the lines of the 13 October install were lost. Two backups keep the file and the two before it.
    [TestMethod]
    public void RollingKeepsTwoBackups()
    {
        using var temp = new TempFolder();
        var log = new FileLog(temp.Path, maxBytes: 2048);

        // About 40 bytes a line, 50 lines a file: 400 lines are 8 files' worth.
        for (int i = 0; i < 400; i++)
        {
            log.Info("line " + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.IsTrue(File.Exists(log.FilePath));
        Assert.IsTrue(File.Exists(log.RolledFilePath));
        Assert.IsTrue(File.Exists(log.SecondRolledFilePath), "There is no second backup.");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "earshot.3.log")), "A third backup is kept.");
        Assert.IsLessThanOrEqualTo(2048L, new FileInfo(log.SecondRolledFilePath).Length);
        int firstOfCurrent = FirstLine(log.FilePath);
        int firstOfFirst = FirstLine(log.RolledFilePath);
        int firstOfSecond = FirstLine(log.SecondRolledFilePath);
        Assert.IsLessThan(firstOfFirst, firstOfSecond, "The second backup is not older than the first.");
        Assert.IsLessThan(firstOfCurrent, firstOfFirst, "The first backup is not older than the file.");
        Assert.AreEqual(firstOfFirst, LastLine(log.SecondRolledFilePath) + 1, "A line was lost between the second backup and the first.");
        Assert.AreEqual(firstOfCurrent, LastLine(log.RolledFilePath) + 1, "A line was lost between the first backup and the file.");
        Assert.AreEqual(399, LastLine(log.FilePath));
    }

    private static int FirstLine(string file) => LineNumber(File.ReadLines(file).First());

    private static int LastLine(string file) => LineNumber(File.ReadLines(file).Last());

    private static int LineNumber(string entry) => int.Parse(entry[^4..], System.Globalization.CultureInfo.InvariantCulture);

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

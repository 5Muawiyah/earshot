using System.Diagnostics;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// Fix connection started by a program that has no console: the tray and Setup.exe are Windows-subsystem programs, so the fix
// they start has no standard output and no parent console to attach to. This runs the real, built Earshot.exe that way.
//
// A test host has a console its children can attach to, so a plain launch cannot reproduce it. Here `start` (cmd.exe) launches
// the program and ends at once; by the time the program asks for its parent's console the parent is gone and AttachConsole
// fails, which is the case the tray's child is in. The run is in safe mode against a temporary data folder, where fix prints its
// plan and changes nothing.
[TestClass]
public sealed class NoConsoleFixRunTests
{
    [TestMethod]
    public void TheRealFixVerbWithNoConsoleWritesItsOutputToTheLog()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        using var runtime = new RuntimeVariablesCleared();
        using var temp = new TempFolder();
        string personsLog = Path.Combine(temp.File("data"), "Local", "Earshot", "logs", "earshot.log");
        string logFile = personsLog;

        // Run as an elevated administrator (as a hosted runner is, and as Setup.exe runs it), Fix writes nothing under the
        // person's profile: its log goes to the machine folder's log, which is used only when that folder is administrators'
        // own. The folder is made that way here, as Setup makes the real one.
        bool elevated = Program.HoldsFixLog(WindowsProcessToken.Current());
        if (elevated)
        {
            string machineFolder = Paths.FromEnvironment(n => n == Paths.DataRootVariable ? temp.File("data") : n == Paths.SafeModeVariable ? "1" : null).MachineFolder;
            StepOutcome made = new NtfsFolderSecurity().CreateHardened(machineFolder);
            Assert.IsTrue(made.Ok, made.CodeName + " " + made.Detail);
            logFile = Path.Combine(machineFolder, "logs", "earshot.log");
        }

        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "/c", "start", "", exe, "fix" })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment[Paths.SafeModeVariable] = "1";
        info.Environment[Paths.DataRootVariable] = temp.File("data");
        using (Process starter = Process.Start(info)!)
        {
            Assert.IsTrue(starter.WaitForExit(TimeSpan.FromSeconds(30)), "cmd.exe did not end.");
        }

        string log = WaitForLog(logFile, "fix output: ", "no console to write to");

        Assert.IsFalse(log.Contains("no console to write to", StringComparison.Ordinal), "Fix found nowhere to write its output: " + log);
        Assert.IsTrue(log.Contains("fix output: " + FixConnection.PlanOnlyNote, StringComparison.Ordinal), log);
        if (elevated)
        {
            Assert.IsFalse(ReadShared(personsLog).Contains("fix output: ", StringComparison.Ordinal), "An elevated Fix wrote into the person's log.");
        }
    }

    private static string WaitForLog(string file, params string[] anyOf)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        string text = "";
        while (DateTime.UtcNow < until)
        {
            text = ReadShared(file);
            if (anyOf.Any(fragment => text.Contains(fragment, StringComparison.Ordinal)))
            {
                return text;
            }

            Thread.Sleep(100);
        }

        return text;
    }

    private static string ReadShared(string file)
    {
        if (!File.Exists(file))
        {
            return "";
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

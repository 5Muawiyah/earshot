using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// Read-only checks of the early-bound Task Scheduler declarations against the local service.
//
// They connect, read folders and one Microsoft task, and build a task definition in memory with NewTask
// to read its XML back. Nothing is registered, run, stopped, changed or deleted: RegisterTask,
// RegisterTaskDefinition, Run, RunEx, Stop, CreateFolder, DeleteFolder, DeleteTask and every
// SetSecurityDescriptor are never called.
[TestClass]
[TestCategory("ReadOnlySystem")]
public sealed class TaskSchedulerReadOnlyTests
{
    private const string SilentCleanup = "\\Microsoft\\Windows\\DiskCleanup\\SilentCleanup";
    private const string SampleExe = "C:\\Program Files\\Earshot\\Earshot.exe";
    private const string SampleArguments = "gate $(Arg0) $(Arg1) $(Arg2)";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ConnectsAndReadsFoldersAndARegisteredTask()
    {
        RunAndReport(report =>
        {
            int createHr = TaskSchedulerCom.TryCreateService(out ITaskService? service);
            Assert.AreEqual(0, createHr, "CoCreateInstance(TaskScheduler) " + NativeCodes.Name(createHr));
            Assert.IsNotNull(service);
            ITaskFolder? root = null;
            try
            {
                int hr = service.Connect(null, null, null, null);
                Assert.AreEqual(0, hr, "Connect " + NativeCodes.Name(hr));
                Assert.AreEqual(0, service.get_Connected(out bool connected));
                Assert.IsTrue(connected);
                Assert.AreEqual(0, service.get_HighestVersion(out uint version));
                Assert.AreEqual(0, service.get_ConnectedUser(out string? user));
                report.Add("Connected: highest version 0x" + version.ToString("X", CultureInfo.InvariantCulture) + ", user " + user);

                hr = service.GetFolder("\\", out root);
                Assert.AreEqual(0, hr, "GetFolder(\\) " + NativeCodes.Name(hr));
                Assert.IsNotNull(root);
                Assert.AreEqual(0, root.get_Path(out string? rootPath));
                Assert.AreEqual("\\", rootPath);
                int sdHr = root.GetSecurityDescriptor(TaskSchedulerCom.DACL_SECURITY_INFORMATION, out string? rootSddl);
                report.Add("Root folder DACL (" + NativeCodes.Name(sdHr) + "): " + rootSddl);

                int earshotHr = service.GetFolder("\\Earshot", out ITaskFolder? earshot);
                report.Add("GetFolder(\\Earshot): " + NativeCodes.Name(earshotHr));
                if (earshot is not null)
                {
                    Marshal.ReleaseComObject(earshot);
                }
                else
                {
                    Assert.AreEqual(TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND, earshotHr, "A missing folder surfaces as ERROR_FILE_NOT_FOUND.");
                }

                int missingHr = root.GetTask("\\Earshot\\NoSuchTask", out IRegisteredTask? missing);
                report.Add("GetTask(\\Earshot\\NoSuchTask): " + NativeCodes.Name(missingHr));
                Assert.IsNull(missing);
                Assert.AreEqual(TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND, missingHr, "A missing task surfaces as ERROR_FILE_NOT_FOUND.");

                hr = root.GetTask(SilentCleanup, out IRegisteredTask? task);
                if (hr < 0 || task is null)
                {
                    report.Add("GetTask(" + SilentCleanup + "): " + NativeCodes.Name(hr));
                    Assert.Inconclusive("The Microsoft SilentCleanup task is not readable on this machine.");
                    return;
                }

                try
                {
                    ReadRegisteredTask(task, report);
                }
                finally
                {
                    Marshal.ReleaseComObject(task);
                }
            }
            finally
            {
                if (root is not null)
                {
                    Marshal.ReleaseComObject(root);
                }

                Marshal.ReleaseComObject(service);
            }
        });
    }

    [TestMethod]
    public void BuildsAnUnregisteredGateDefinitionAndReadsItsXml()
    {
        RunAndReport(report =>
        {
            int createHr = TaskSchedulerCom.TryCreateService(out ITaskService? service);
            Assert.AreEqual(0, createHr, "CoCreateInstance(TaskScheduler) " + NativeCodes.Name(createHr));
            Assert.IsNotNull(service);
            var created = new List<object>();
            try
            {
                int hr = service.Connect(null, null, null, null);
                Assert.AreEqual(0, hr, "Connect " + NativeCodes.Name(hr));

                hr = service.NewTask(0, out ITaskDefinition? definition);
                Assert.AreEqual(0, hr, "NewTask " + NativeCodes.Name(hr));
                Assert.IsNotNull(definition);
                created.Add(definition);

                Assert.AreEqual(0, definition.get_RegistrationInfo(out IRegistrationInfo? info));
                Assert.IsNotNull(info);
                created.Add(info);
                Assert.AreEqual(0, info.put_Description("Interop read-only test. Never registered."));

                Assert.AreEqual(0, definition.get_Principal(out IPrincipal? principal));
                Assert.IsNotNull(principal);
                created.Add(principal);
                Assert.AreEqual(0, principal.put_UserId("S-1-5-18"));
                Assert.AreEqual(0, principal.put_LogonType(TaskSchedulerCom.TASK_LOGON_SERVICE_ACCOUNT));

                Assert.AreEqual(0, definition.get_Settings(out ITaskSettings? settings));
                Assert.IsNotNull(settings);
                created.Add(settings);
                Assert.AreEqual(0, settings.put_Enabled(true));
                Assert.AreEqual(0, settings.put_AllowDemandStart(true));
                Assert.AreEqual(0, settings.put_DisallowStartIfOnBatteries(false));
                Assert.AreEqual(0, settings.put_StopIfGoingOnBatteries(false));
                Assert.AreEqual(0, settings.put_ExecutionTimeLimit("PT2M"));
                Assert.AreEqual(0, settings.put_MultipleInstances(TaskSchedulerCom.TASK_INSTANCES_QUEUE));
                Assert.AreEqual(0, settings.put_Compatibility(TaskSchedulerCom.TASK_COMPATIBILITY_V2_4));

                Assert.AreEqual(0, definition.get_Actions(out IActionCollection? actions));
                Assert.IsNotNull(actions);
                created.Add(actions);
                Assert.AreEqual(0, actions.Create(TaskSchedulerCom.TASK_ACTION_EXEC, out IAction? action));
                Assert.IsNotNull(action);
                created.Add(action);
                var exec = (IExecAction)action;
                Assert.AreEqual(0, exec.put_Path(SampleExe));
                Assert.AreEqual(0, exec.put_Arguments(SampleArguments));
                Assert.AreEqual(0, exec.put_WorkingDirectory("C:\\Program Files\\Earshot"));

                Assert.AreEqual(0, definition.get_Triggers(out ITriggerCollection? triggers));
                Assert.IsNotNull(triggers);
                created.Add(triggers);
                Assert.AreEqual(0, triggers.Create(TaskSchedulerCom.TASK_TRIGGER_BOOT, out ITrigger? trigger));
                Assert.IsNotNull(trigger);
                created.Add(trigger);
                var boot = (IBootTrigger)trigger;
                Assert.AreEqual(0, boot.put_Delay("PT30S"));

                hr = definition.get_XmlText(out string? xml);
                Assert.AreEqual(0, hr, "get_XmlText " + NativeCodes.Name(hr));
                Assert.IsNotNull(xml);
                report.Add(xml);

                // Read everything back through the getters, which exercises each slot's neighbours.
                // The service resolves the S-1-5-18 SID it was given to the account name SYSTEM (observed on
                // this machine), so a read-back check of the principal must accept either form.
                Assert.AreEqual(0, principal.get_UserId(out string? userId));
                report.Add("Principal UserId read back as: " + userId);
                Assert.IsTrue(userId is "S-1-5-18" or "SYSTEM", "UserId read back as " + userId);
                Assert.AreEqual(0, principal.get_LogonType(out int logonType));
                Assert.AreEqual(TaskSchedulerCom.TASK_LOGON_SERVICE_ACCOUNT, logonType);
                Assert.AreEqual(0, settings.get_ExecutionTimeLimit(out string? limit));
                Assert.AreEqual("PT2M", limit);
                Assert.AreEqual(0, settings.get_MultipleInstances(out int policy));
                Assert.AreEqual(TaskSchedulerCom.TASK_INSTANCES_QUEUE, policy);
                Assert.AreEqual(0, settings.get_DisallowStartIfOnBatteries(out bool disallow));
                Assert.IsFalse(disallow);
                Assert.AreEqual(0, settings.get_StopIfGoingOnBatteries(out bool stop));
                Assert.IsFalse(stop);
                Assert.AreEqual(0, settings.get_AllowDemandStart(out bool demand));
                Assert.IsTrue(demand);
                Assert.AreEqual(0, settings.get_Compatibility(out int compatibility));
                Assert.AreEqual(TaskSchedulerCom.TASK_COMPATIBILITY_V2_4, compatibility);
                Assert.AreEqual(0, actions.get_Count(out int actionCount));
                Assert.AreEqual(1, actionCount);
                Assert.AreEqual(0, exec.get_Type(out int actionType));
                Assert.AreEqual(TaskSchedulerCom.TASK_ACTION_EXEC, actionType);
                Assert.AreEqual(0, exec.get_Path(out string? path));
                Assert.AreEqual(SampleExe, path);
                Assert.AreEqual(0, exec.get_Arguments(out string? arguments));
                Assert.AreEqual(SampleArguments, arguments);
                Assert.AreEqual(0, triggers.get_Count(out int triggerCount));
                Assert.AreEqual(1, triggerCount);
                Assert.AreEqual(0, boot.get_Type(out int triggerType));
                Assert.AreEqual(TaskSchedulerCom.TASK_TRIGGER_BOOT, triggerType);
                Assert.AreEqual(0, boot.get_Delay(out string? delay));
                Assert.AreEqual("PT30S", delay);

                Assert.IsTrue(
                    xml.Contains("<UserId>S-1-5-18</UserId>", StringComparison.Ordinal) || xml.Contains("<UserId>SYSTEM</UserId>", StringComparison.Ordinal),
                    "The XML principal is not Local System.");
                StringAssert.Contains(xml, "<BootTrigger>");
                StringAssert.Contains(xml, "<Delay>PT30S</Delay>");
                StringAssert.Contains(xml, "<Command>" + SampleExe + "</Command>");
                StringAssert.Contains(xml, "<Arguments>" + SampleArguments + "</Arguments>");
                StringAssert.Contains(xml, "<ExecutionTimeLimit>PT2M</ExecutionTimeLimit>");
                StringAssert.Contains(xml, "<MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>");
                StringAssert.Contains(xml, "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
                StringAssert.Contains(xml, "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            }
            finally
            {
                created.Reverse();
                foreach (object comObject in created)
                {
                    Marshal.ReleaseComObject(comObject);
                }

                Marshal.ReleaseComObject(service);
            }
        });
    }

    private static void ReadRegisteredTask(IRegisteredTask task, List<string> report)
    {
        Assert.AreEqual(0, task.get_Name(out string? name));
        Assert.AreEqual("SilentCleanup", name);
        Assert.AreEqual(0, task.get_Path(out string? path));
        Assert.AreEqual(SilentCleanup, path);
        Assert.AreEqual(0, task.get_State(out int state));
        Assert.IsTrue(state is >= TaskSchedulerCom.TASK_STATE_UNKNOWN and <= TaskSchedulerCom.TASK_STATE_RUNNING, "TASK_STATE " + state);
        Assert.AreEqual(0, task.get_Enabled(out bool enabled));
        int resultHr = task.get_LastTaskResult(out int lastResult);
        int runHr = task.get_LastRunTime(out double lastRun);
        int xmlHr = task.get_Xml(out string? xml);
        int sdHr = task.GetSecurityDescriptor(TaskSchedulerCom.DACL_SECURITY_INFORMATION, out string? sddl);
        report.Add(path + ": state " + state + ", enabled " + enabled + ", last result " + NativeCodes.Name(lastResult) +
                   " (" + NativeCodes.Name(resultHr) + "), last run " + (runHr < 0 ? NativeCodes.Name(runHr) : DateTime.FromOADate(lastRun).ToString("u", CultureInfo.InvariantCulture)) +
                   ", xml " + (xml?.Length ?? 0) + " chars (" + NativeCodes.Name(xmlHr) + ")");
        report.Add("  DACL (" + NativeCodes.Name(sdHr) + "): " + sddl);
        Assert.AreEqual(0, xmlHr, "get_Xml");
        StringAssert.Contains(xml, "<Task");

        Assert.AreEqual(0, task.get_Definition(out ITaskDefinition? definition));
        Assert.IsNotNull(definition);
        IPrincipal? principal = null;
        IActionCollection? actions = null;
        IAction? action = null;
        try
        {
            Assert.AreEqual(0, definition.get_Principal(out principal));
            Assert.IsNotNull(principal);
            Assert.AreEqual(0, principal.get_GroupId(out string? group));
            Assert.AreEqual(0, principal.get_RunLevel(out int runLevel));
            Assert.AreEqual(0, definition.get_Actions(out actions));
            Assert.IsNotNull(actions);
            Assert.AreEqual(0, actions.get_Count(out int count));
            string firstAction = "none";
            if (count > 0)
            {
                Assert.AreEqual(0, actions.get_Item(1, out action));
                Assert.IsNotNull(action);
                Assert.AreEqual(0, action.get_Type(out int type));
                firstAction = "type " + type;
                if (type == TaskSchedulerCom.TASK_ACTION_EXEC)
                {
                    Assert.AreEqual(0, ((IExecAction)action).get_Path(out string? command));
                    firstAction += " " + command;
                }
            }

            report.Add("  principal group " + group + ", run level " + runLevel + ", actions " + count + ", first " + firstAction);
        }
        finally
        {
            if (action is not null)
            {
                Marshal.ReleaseComObject(action);
            }

            if (actions is not null)
            {
                Marshal.ReleaseComObject(actions);
            }

            if (principal is not null)
            {
                Marshal.ReleaseComObject(principal);
            }

            Marshal.ReleaseComObject(definition);
        }
    }

    private void RunAndReport(Action<List<string>> body)
    {
        var report = new List<string>();
        try
        {
            MtaThread.Run(() => body(report), TimeSpan.FromSeconds(60));
        }
        finally
        {
            foreach (string line in report)
            {
                TestContext.WriteLine(line);
            }
        }
    }
}

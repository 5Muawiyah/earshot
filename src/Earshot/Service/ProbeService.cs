using System.Globalization;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Service;

namespace Earshot;

// probe service: how the hand-back service is registered and whether it runs, read from the service control manager
// with the rights every signed-in user holds (query and read control). Read-only and never elevated: nothing is
// registered, started, stopped, configured or deleted.
internal static partial class Program
{
    static partial void ProbeService(ProbeContext ctx)
    {
        Paths paths = Paths.Current;
        ctx.ExitCode = WriteServiceProbe(ctx, new WindowsServiceControl(), paths.InstallFolder);
        ctx.Handled = true;
    }

    // The report, and the exit code: Ok when the service is registered or is not there (both are answers), OsError when
    // the control manager could not say.
    internal static int WriteServiceProbe(ProbeContext ctx, IServiceControl service, string installFolder)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(service);
        ServiceSpec spec = ServicePlan.Spec(installFolder);
        ServiceQuery read = service.Query(ServicePlan.ServiceName);
        IReadOnlyList<string> problems = read.Presence == ServicePresence.Present ? ServiceCheck.Verify(read, spec) : [];
        string summary = HandBackServiceText.RegistrationClause(read, spec);
        string state = read.Presence == ServicePresence.Present ? ServiceSteps.StateName(read.State) : "";

        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "service");
                w.WriteString("name", ServicePlan.ServiceName);
                w.WriteBoolean("present", read.Presence == ServicePresence.Present);
                w.WriteBoolean("readable", read.Presence != ServicePresence.Unknown);
                w.WriteString("summary", summary);
                w.WriteString("state", state);
                WriteNumber(w, "stateCode", read.State);
                WriteNumber(w, "startType", read.StartType);
                WriteNumber(w, "serviceType", read.ServiceType);
                WriteNumber(w, "processId", read.ProcessId);
                w.WriteString("imagePath", read.ImagePath);
                w.WriteString("account", read.Account);
                w.WriteString("displayName", read.DisplayName);
                w.WriteString("description", read.Description);
                WriteNumber(w, "preshutdownMs", read.PreshutdownTimeoutMs);
                w.WriteString("sddl", read.Sddl);
                w.WriteStartArray("problems");
                foreach (string problem in problems)
                {
                    w.WriteStringValue(problem);
                }

                w.WriteEndArray();
                w.WriteStartArray("steps");
                foreach (StepOutcome step in read.Steps)
                {
                    w.WriteStartObject();
                    w.WriteString("step", step.Step);
                    w.WriteBoolean("ok", step.Ok);
                    w.WriteNumber("code", step.Code);
                    w.WriteString("codeName", step.CodeName);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
        }
        else
        {
            TextWriter o = ctx.Out;
            o.WriteLine("service " + ServicePlan.ServiceName + ": " + summary);
            if (read.Presence == ServicePresence.Present)
            {
                o.WriteLine("  state " + state + ", start type " + Show(read.StartType) + ", process " + Show(read.ProcessId));
                o.WriteLine("  image path " + (read.ImagePath ?? "not read"));
                o.WriteLine("  account " + (read.Account ?? "not read") + ", pre-shutdown time-out " + Show(read.PreshutdownTimeoutMs) + " ms");
                o.WriteLine("  access list " + (read.Sddl ?? "not read"));
                foreach (string problem in problems)
                {
                    o.WriteLine("  differs: " + problem);
                }
            }

            foreach (StepOutcome step in read.Steps.Where(s => !s.Ok))
            {
                o.WriteLine("  " + step.Step + " " + step.CodeName + " (" + step.Code.ToString(CultureInfo.InvariantCulture) + ")");
            }
        }

        return read.Presence == ServicePresence.Unknown ? ExitCodes.OsError : ExitCodes.Ok;
    }

    private static void WriteNumber(System.Text.Json.Utf8JsonWriter w, string name, uint? value)
    {
        if (value is uint v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static string Show(uint? value) => value is uint v ? v.ToString(CultureInfo.InvariantCulture) : "not read";
}

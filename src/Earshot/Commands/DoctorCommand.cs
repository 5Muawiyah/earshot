using Earshot.Contracts;

namespace Earshot.Commands;

// Earshot.exe doctor: reads this PC through a source and prints the summary and the JSON. Nothing is changed.
internal static class DoctorCommand
{
    public static int Run(IDoctorSource source, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        output.Write(DoctorReport.Output(source.Read()));
        return ExitCodes.Ok;
    }
}

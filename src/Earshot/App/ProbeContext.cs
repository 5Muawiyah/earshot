using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Earshot.Composition;

namespace Earshot.App;

// What a probe target gets. A target implementation must:
//   - set Handled = true (a target that leaves it false is reported as not available);
//   - leave ExitCode at 0 on success, or set a non-zero ExitCodes value when it could not read;
//   - write plain text to Out, or, when Json is true, exactly one JSON value (use WriteJson);
//     in text mode the dispatcher has already printed the "== target ==" heading;
//   - change nothing: no KS property sends, no node, service or task changes.
internal sealed class ProbeContext
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // Output goes to a console or a file, never into HTML, so names such as the curly
        // apostrophe in a device name stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<ServiceRegistry> _services;

    public ProbeContext(string target, TextWriter output, bool json, Func<ServiceRegistry> services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(services);
        Target = target;
        Out = output;
        Json = json;
        _services = services;
    }

    public string Target { get; }

    public TextWriter Out { get; }

    public bool Json { get; }

    // Built on first use, with the same composition as the tray (safe mode applies).
    public ServiceRegistry Services => _services();

    public bool Handled { get; set; }

    public int ExitCode { get; set; }

    // Writes one JSON value built with a Utf8JsonWriter, followed by a line break.
    public void WriteJson(Action<Utf8JsonWriter> write) => Out.WriteLine(JsonText(write));

    internal static string JsonText(Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

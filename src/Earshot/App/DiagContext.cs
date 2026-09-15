using System.Globalization;
using System.Text;
using Earshot.Composition;

namespace Earshot.App;

// What a diag target gets. diag performs live single-shot actions for the owner's live tests and
// is refused outright in safe mode (except read-only targets) before a target runs. A target
// implementation must set Handled = true, set ExitCode when it fails, write a short human summary
// to Out and its JSON evidence to a file from NewEvidenceFile.
internal sealed class DiagContext
{
    private readonly Func<ServiceRegistry> _services;

    public DiagContext(string target, IReadOnlyList<string> args, string evidenceFolder, TextWriter output, Func<ServiceRegistry> services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceFolder);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(services);
        Target = target;
        Args = args;
        EvidenceFolder = evidenceFolder;
        Out = output;
        _services = services;
    }

    public string Target { get; }

    // The arguments after the target, already checked against the target's grammar.
    public IReadOnlyList<string> Args { get; }

    // %LOCALAPPDATA%\Earshot\livetest, or its EARSHOT_DATA_ROOT redirect.
    public string EvidenceFolder { get; }

    public TextWriter Out { get; }

    // Built on first use, with the same composition as the tray.
    public ServiceRegistry Services => _services();

    public bool Handled { get; set; }

    public int ExitCode { get; set; }

    // A new evidence file path: <EvidenceFolder>\<UTC yyyyMMdd'T'HHmmssfff'Z'>-<label>.json.
    // Creates the folder. The label is reduced to lower-case letters, digits and hyphens.
    public string NewEvidenceFile(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var safe = new StringBuilder(label.Length);
        foreach (char c in label.ToLowerInvariant())
        {
            safe.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-');
        }

        Directory.CreateDirectory(EvidenceFolder);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(EvidenceFolder, stamp + "-" + safe + ".json");
    }
}

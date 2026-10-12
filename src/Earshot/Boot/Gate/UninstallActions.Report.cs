using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// What the caller of uninstall can pass in beyond the command line.
//   SettingsPinAirPods  the signed-in user's own settings.json pins an AirPods. The elevated run cannot read that file
//                       safely, so the caller (which runs as the user) says it.
//   ChosenAddress       the 12 hex characters of the AirPods the user picked on screen, for a PC where Earshot left no
//                       record of which pair it blocked and several pairs are paired. The pick says which pair; it is not
//                       evidence that Earshot was set up.
internal sealed record UninstallOptions
{
    public bool SettingsPinAirPods { get; init; }

    public string? ChosenAddress { get; init; }
}

// What uninstall put back and what it could not, in plain words for the caller to show. No line carries an instance id,
// an address, a container or a native code: those are in the steps. ChooseFrom lists the names of the AirPods the user has
// to pick from when Earshot left no record and more than one pair is paired; the caller asks, then runs uninstall again
// with UninstallOptions.ChosenAddress.
internal sealed record UninstallReport(
    IReadOnlyList<string> PutBack,
    IReadOnlyList<string> NotPutBack,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> ChooseFrom)
{
    public static UninstallReport Empty { get; } = new([], [], [], []);

    public bool NeedsChoice => ChooseFrom.Count > 0;

    // Everything to show, in order: what was put back, what was not, then the rest.
    public IReadOnlyList<string> Lines => [.. PutBack, .. NotPutBack, .. Notes];
}

internal sealed record UninstallRun(InstallResult Result, UninstallReport Report);

// Collects the report while uninstall runs.
internal sealed class UninstallReportBuilder
{
    private readonly List<string> _putBack = [];
    private readonly List<string> _notPutBack = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _chooseFrom = [];

    // Nodes by plain name: the AirPods themselves, or a part of them.
    public void Nodes(IReadOnlyCollection<TargetNode> nodes, bool restored)
    {
        int device = nodes.Count(n => n.IsDeviceNode);
        int parts = nodes.Count - device;
        if (device > 0)
        {
            (restored ? _putBack : _notPutBack).Add(restored ? "The AirPods were turned back on." : "The AirPods could not be turned back on.");
        }

        if (parts == 1)
        {
            (restored ? _putBack : _notPutBack).Add(restored ? "Part of the AirPods was turned back on." : "Part of the AirPods could not be turned back on.");
        }
        else if (parts > 1)
        {
            string count = parts.ToString(System.Globalization.CultureInfo.InvariantCulture);
            (restored ? _putBack : _notPutBack).Add(restored
                ? count + " parts of the AirPods were turned back on."
                : count + " parts of the AirPods could not be turned back on.");
        }
    }

    public void UnnamedNodeFailure() => _notPutBack.Add("Part of the AirPods could not be turned back on.");

    public void PutBack(string line) => _putBack.Add(line);

    public void NotPutBack(string line) => _notPutBack.Add(line);

    public void Note(string line) => _notes.Add(line);

    public void Choose(IEnumerable<string> names) => _chooseFrom.AddRange(names);

    public UninstallReport Build() => new([.. _putBack], [.. _notPutBack], [.. _notes], [.. _chooseFrom]);
}

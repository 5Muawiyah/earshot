using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// update-outcome.json: how the last update ended (UpdateOutcome). Read and written like the other files here: a
// strict shape, a size cap, and a temporary file moved into place.
internal sealed partial class GateStore
{
    public const string UpdateOutcomeFileName = "update-outcome.json";
    public const int MaxOutcomeText = 300;

    public string UpdateOutcomeFile => Path.Combine(Folder, UpdateOutcomeFileName);

    public GateRead<UpdateOutcome> ReadUpdateOutcome() => Read<UpdateOutcome>(UpdateOutcomeFile, "read-update-outcome", root =>
    {
        string? shape = RequireShape(
            root,
            ("SchemaVersion", JsonValueKind.Number),
            ("Id", JsonValueKind.String),
            ("WrittenUtc", JsonValueKind.String),
            ("Kind", JsonValueKind.String),
            ("Version", JsonValueKind.String),
            ("Reason", JsonValueKind.String),
            ("Code", JsonValueKind.String));
        if (shape is not null)
        {
            return (null, shape);
        }

        if (!root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != SchemaVersion)
        {
            return (null, "SchemaVersion is not " + SchemaVersion + ".");
        }

        string id = root.GetProperty("Id").GetString() ?? "";
        if (!BoundaryValidation.IsNonce(id))
        {
            return (null, "Id is not a 32 character lower-case hex value.");
        }

        if (!TryReadTime(root, "WrittenUtc", out DateTimeOffset written))
        {
            return (null, "WrittenUtc is not ISO 8601 UTC.");
        }

        if (!Enum.TryParse(root.GetProperty("Kind").GetString(), ignoreCase: false, out UpdateOutcomeKind kind) || !Enum.IsDefined(kind))
        {
            return (null, "Kind is not an update outcome.");
        }

        string releaseVersion = root.GetProperty("Version").GetString() ?? "";
        string reason = root.GetProperty("Reason").GetString() ?? "";
        string code = root.GetProperty("Code").GetString() ?? "";
        if (releaseVersion.Length > 40 || reason.Length > MaxOutcomeText || code.Length > 64 || reason.Any(char.IsControl) || releaseVersion.Any(char.IsControl) || code.Any(char.IsControl))
        {
            return (null, "A text is too long or holds a control character.");
        }

        return (new UpdateOutcome(id, written, kind, releaseVersion, reason, code), null);
    });

    public StepOutcome WriteUpdateOutcome(UpdateOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!BoundaryValidation.IsNonce(outcome.Id))
        {
            return StepOutcomes.NotAttempted("write-update-outcome", "An update outcome needs a 32 character lower-case hex Id.");
        }

        return Write(UpdateOutcomeFile, "write-update-outcome", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("SchemaVersion", SchemaVersion);
            w.WriteString("Id", outcome.Id);
            w.WriteString("WrittenUtc", FormatTime(outcome.WrittenUtc));
            w.WriteString("Kind", outcome.Kind.ToString());
            w.WriteString("Version", Bound(outcome.Version, 40));
            w.WriteString("Reason", Bound(outcome.Reason, MaxOutcomeText));
            w.WriteString("Code", Bound(outcome.Code, 64));
            w.WriteEndObject();
        });
    }
}

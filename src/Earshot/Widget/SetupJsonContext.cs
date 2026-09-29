using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Earshot.Widget;

// The set-up record and the proof summary, camel-case with enums as their names. Separate from
// WidgetJsonContext, whose claim.json keeps the member names it always had. A member missing from a
// hand-written file is refused rather than read as zero.
// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
[JsonSourceGenerationOptions(
    WriteIndented = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BatterySetupRecord))]
[JsonSerializable(typeof(DecodeProofFile))]
internal sealed partial class SetupJsonContext : JsonSerializerContext
{
}

// widget\proof.json. Derived from the records except BroadcastWhilePlayingFromThisPc, which is a run-time
// observation that is read back at start.
internal sealed record DecodeProofFile(
    int SchemaVersion,
    DateTimeOffset EvaluatedAtUtc,
    int Records,
    Dictionary<string, ProofFileField> Fields,
    BroadcastObservation BroadcastWhilePlayingFromThisPc,
    IReadOnlyList<string> Notes);

internal sealed record ProofFileField(FieldProofStatus Status, JsonNode? Value, int Agree, int Disagree);

// Owned messages that arrived while this PC rendered to the AirPods.
internal sealed record BroadcastObservation(int OwnedMessages, DateTimeOffset? FirstAtUtc, DateTimeOffset? LastAtUtc, bool Proved);

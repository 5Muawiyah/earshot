using System.Text.Json.Serialization;

namespace Earshot.Widget;

// The widget's own source-generated JSON context, so claim.json never goes through
// Earshot.Infra.SettingsJsonContext: it is not a member of settings.json and is not roamed. A member
// missing from a hand-written file is refused rather than read as zero.
// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
[JsonSourceGenerationOptions(WriteIndented = true, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WidgetClaim))]
internal sealed partial class WidgetJsonContext : JsonSerializerContext
{
}

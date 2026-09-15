using System.Text.Json.Serialization;
using Earshot.Contracts;

namespace Earshot.Infra;

// Source-generated serializers for Earshot's JSON files. Reflection-based serialization is off
// for the whole app (JsonSerializerIsReflectionEnabledByDefault=false).
// RespectNullableAnnotations makes an explicit null for a non-nullable string a JsonException
// instead of a null slipping into the settings. Unknown members are ignored (the default).
// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/nullable-annotations
[JsonSourceGenerationOptions(WriteIndented = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(EarshotSettings))]
[JsonSerializable(typeof(GateConfig))]
[JsonSerializable(typeof(DeviceIdentity))]
[JsonSerializable(typeof(ProtectionRecord))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

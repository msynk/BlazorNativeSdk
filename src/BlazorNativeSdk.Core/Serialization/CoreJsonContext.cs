using System.Text.Json.Serialization;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowCheckpoint))]
[JsonSerializable(typeof(WindowPlacement))]
[JsonSerializable(typeof(WorkRecord))]
[JsonSerializable(typeof(TransferRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

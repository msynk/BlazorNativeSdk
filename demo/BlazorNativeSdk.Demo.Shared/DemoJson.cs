using System.Text.Json.Serialization;

namespace BlazorNativeSdk.Demo;

/// <summary>Source-generated JSON metadata for demo checkpoints.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class DemoJson : JsonSerializerContext;

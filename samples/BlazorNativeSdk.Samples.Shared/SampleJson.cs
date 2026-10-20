using System.Text.Json.Serialization;

namespace BlazorNativeSdk.Samples;

/// <summary>Source-generated JSON metadata for sample checkpoints.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class SampleJson : JsonSerializerContext;

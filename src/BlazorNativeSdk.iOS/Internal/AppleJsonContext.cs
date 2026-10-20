using System.Text.Json.Serialization;
using BlazorNativeSdk.Lifecycle;

namespace BlazorNativeSdk.ApplePlatform;

/// <summary>Scheduling metadata the BGTaskScheduler bridge keeps per work item (the Core scheduler does not expose it).</summary>
internal sealed class AppleWorkMetadata
{
    public bool IsLongRunning { get; set; }

    public WorkConstraints Constraints { get; set; }
}

/// <summary>Source-generated JSON metadata (trimming and AOT safe).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(AppleWorkMetadata))]
internal sealed partial class AppleJsonContext : JsonSerializerContext
{
}

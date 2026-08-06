using System.Text.Json.Serialization;

namespace Lutra.Core.Recovery;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EnvironmentRecoveryDescriptor
{
    [JsonPropertyName("format_version")]
    public required int FormatVersion { get; init; }

    [JsonPropertyName("artifact_id")]
    public required string ArtifactId { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTime CreatedAt { get; init; }

    [JsonPropertyName("artifact_file_name")]
    public required string ArtifactFileName { get; init; }

    [JsonPropertyName("file_size_bytes")]
    public required long FileSizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("lutra_version")]
    public required string LutraVersion { get; init; }

    [JsonPropertyName("sources")]
    public required List<EnvironmentRecoveryDescriptorSource> Sources { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EnvironmentRecoveryDescriptorSource
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required EnvironmentRecoverySourceKind Kind { get; init; }
}

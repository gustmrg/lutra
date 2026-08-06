using System.Text.Json.Serialization;

namespace Lutra.Core.Recovery;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EnvironmentRecoveryManifest
{
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("format_version")]
    public required int FormatVersion { get; init; }

    [JsonPropertyName("artifact_id")]
    public required string ArtifactId { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTime CreatedAt { get; init; }

    [JsonPropertyName("lutra_version")]
    public required string LutraVersion { get; init; }

    [JsonPropertyName("sources")]
    public required List<EnvironmentRecoverySource> Sources { get; init; }

    [JsonPropertyName("required_tools")]
    public List<string> RequiredTools { get; init; } = [];

    [JsonPropertyName("systemd_units")]
    public List<string> SystemdUnits { get; init; } = [];

    [JsonPropertyName("docker_containers")]
    public List<string> DockerContainers { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EnvironmentRecoverySource
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required EnvironmentRecoverySourceKind Kind { get; init; }

    [JsonPropertyName("payload_path")]
    public required string PayloadPath { get; init; }

    [JsonPropertyName("size_bytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

public enum EnvironmentRecoverySourceKind
{
    File,
    Volume
}

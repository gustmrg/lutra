using System.Text.Json.Serialization;

namespace Lutra.Core.Recovery;

public sealed class EnvironmentBackupReport
{
    [JsonPropertyName("artifact_id")]
    public required string ArtifactId { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTime StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public required DateTime CompletedAt { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sources")]
    public required List<EnvironmentBackupReportSource> Sources { get; init; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];
}

public sealed class EnvironmentBackupReportSource
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required EnvironmentRecoverySourceKind Kind { get; init; }

    [JsonPropertyName("excluded_entries")]
    public List<string> ExcludedEntries { get; init; } = [];
}

public sealed record EnvironmentBackupResult(
    bool Success,
    string? FilePath,
    long? FileSizeBytes,
    string? Sha256,
    string? ErrorMessage,
    DateTime StartedAt,
    TimeSpan Duration);

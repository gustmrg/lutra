using System.Text.Json.Serialization;
using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public sealed class BackupManifest
{
    [JsonPropertyName("target_name")]
    public required string TargetName { get; init; }

    /// <summary>
    /// The kind of target that produced this backup: <c>"database"</c> or <c>"files"</c>.
    /// </summary>
    [JsonPropertyName("target_type")]
    public required string TargetType { get; init; }

    [JsonPropertyName("database_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseType? DatabaseType { get; init; }

    [JsonPropertyName("database")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Database { get; init; }

    [JsonPropertyName("container")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; init; }

    /// <summary>
    /// The configured source paths, present for <c>"files"</c> backups.
    /// </summary>
    [JsonPropertyName("paths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Paths { get; init; }

    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Volume { get; init; }

    [JsonPropertyName("backup_file_name")]
    public required string BackupFileName { get; init; }

    [JsonPropertyName("file_size_bytes")]
    public required long FileSizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("compression")]
    public required CompressionType Compression { get; init; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTime StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public required DateTime CompletedAt { get; init; }

    [JsonPropertyName("duration_ms")]
    public required long DurationMs { get; init; }

    [JsonPropertyName("lutra_version")]
    public required string LutraVersion { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }
}

using System.Text.Json.Serialization;

namespace Lutra.Core.History;

public class BackupRecord
{
    [JsonPropertyName("target_name")]
    public required string TargetName { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTime Timestamp { get; init; }

    [JsonPropertyName("file_name")]
    public required string FileName { get; init; }

    [JsonPropertyName("file_size_bytes")]
    public required long FileSizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }

    [JsonPropertyName("manifest_file_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestFileName { get; init; }

    [JsonPropertyName("duration_ms")]
    public required long DurationMs { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the kind of record. <see langword="null"/> indicates a backup record
    /// (the default for records written before this field existed);
    /// <c>"verify"</c> indicates a test-restore verification record.
    /// </summary>
    [JsonPropertyName("record_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordType { get; init; }
}

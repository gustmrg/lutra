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

    [JsonPropertyName("duration_ms")]
    public required long DurationMs { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("error_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }
}

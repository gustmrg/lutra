namespace Lutra.Core.Backup;

public record BackupResult
{
    public required string TargetName { get; init; }
    public required bool Success { get; init; }
    public required DateTime Timestamp { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? FilePath { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
}

namespace Lutra.Core.Backup;

public record BackupResult
{
    public required string TargetName { get; init; }
    public required bool Success { get; init; }
    public required DateTime Timestamp { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? FilePath { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets a problem that did not fail the backup but needs attention, such as
    /// retention cleanup failing after the artifact was written.
    /// </summary>
    public string? Warning { get; init; }
}

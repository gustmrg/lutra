namespace Lutra.Core.History;

/// <summary>A typed backup-domain operation persisted in Lutra's application database.</summary>
public sealed class HistoryRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TargetName { get; init; }
    public required HistoryOperationType OperationType { get; init; }
    public required HistoryOperationStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Guid? LeaseId { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public string? FileName { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string? ManifestFileName { get; init; }
    public long? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

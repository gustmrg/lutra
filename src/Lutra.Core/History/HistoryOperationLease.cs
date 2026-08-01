namespace Lutra.Core.History;

public sealed record HistoryOperationLease(Guid OperationId, Guid LeaseId);

public sealed record HistoryOperationCompletion(
    string? FileName = null,
    long? FileSizeBytes = null,
    string? Sha256 = null,
    string? ManifestFileName = null,
    long? DurationMs = null);

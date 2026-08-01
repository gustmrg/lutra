namespace Lutra.Core.History;

/// <summary>
/// Manages persistent backup-domain operation history.
/// </summary>
/// <remarks>
/// Implementations must be safe for concurrent access from separate Lutra processes.
/// </remarks>
public interface IBackupHistoryService
{
    Task<HistoryOperationLease> BeginOperationAsync(
        string targetName,
        HistoryOperationType operationType,
        CancellationToken cancellationToken = default);

    Task CompleteOperationAsync(
        Guid operationId,
        Guid leaseId,
        HistoryOperationCompletion completion,
        CancellationToken cancellationToken = default);

    Task FailOperationAsync(
        Guid operationId,
        Guid leaseId,
        string errorMessage,
        long? durationMs = null,
        CancellationToken cancellationToken = default);

    Task CancelOperationAsync(
        Guid operationId,
        Guid leaseId,
        string? errorMessage = null,
        long? durationMs = null,
        CancellationToken cancellationToken = default);

    Task RenewLeaseAsync(
        Guid operationId,
        Guid leaseId,
        CancellationToken cancellationToken = default);

    Task InterruptOperationAsync(
        Guid operationId,
        Guid leaseId,
        string errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a terminal operation record to application history.
    /// </summary>
    /// <param name="record">The backup record to persist.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the record has been written to disk.</returns>
    Task AddRecordAsync(HistoryRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all history records, ordered by start time descending (newest first).
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An immutable list of all backup records.</returns>
    Task<IReadOnlyList<HistoryRecord>> GetAllRecordsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves history records for a specific target, ordered by start time descending.
    /// </summary>
    /// <param name="targetName">
    /// The database target name to filter by. Case-sensitive match against
    /// <see cref="HistoryRecord.TargetName"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An immutable list of backup records matching the target name.</returns>
    Task<IReadOnlyList<HistoryRecord>> GetRecordsByTargetAsync(string targetName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a backup record from history, typically called when the
    /// corresponding backup file is deleted during retention cleanup.
    /// </summary>
    /// <param name="id">The unique history record ID.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the record was found and removed;
    /// <see langword="false"/> if no matching record existed.
    /// </returns>
    Task<bool> RemoveRecordAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Removes old non-backup records and failed backup attempts.</summary>
    Task<int> PruneOperationalRecordsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

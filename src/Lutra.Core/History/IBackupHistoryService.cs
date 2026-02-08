namespace Lutra.Core.History;

/// <summary>
/// Manages persistent backup history records stored as a JSON file in the backup directory.
/// </summary>
/// <remarks>
/// The history file (<c>backup-history.json</c>) resides at the root of the configured
/// backup directory and tracks metadata for every backup attempt, including failures.
/// Implementations must be safe for sequential access but are not required to support
/// concurrent writers.
/// </remarks>
public interface IBackupHistoryService
{
    /// <summary>
    /// Adds a new backup record to the history file.
    /// </summary>
    /// <param name="record">The backup record to persist.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the record has been written to disk.</returns>
    Task AddRecordAsync(BackupRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all backup history records, ordered by timestamp descending (newest first).
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An immutable list of all backup records.</returns>
    Task<IReadOnlyList<BackupRecord>> GetAllRecordsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves backup history records for a specific database target, ordered by
    /// timestamp descending (newest first).
    /// </summary>
    /// <param name="targetName">
    /// The database target name to filter by. Case-sensitive match against
    /// <see cref="BackupRecord.TargetName"/>.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An immutable list of backup records matching the target name.</returns>
    Task<IReadOnlyList<BackupRecord>> GetRecordsByTargetAsync(string targetName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a backup record from the history file, typically called when the
    /// corresponding backup file is deleted during retention cleanup.
    /// </summary>
    /// <param name="targetName">The database target name.</param>
    /// <param name="fileName">The backup file name to remove from history.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the record was found and removed;
    /// <see langword="false"/> if no matching record existed.
    /// </returns>
    Task<bool> RemoveRecordAsync(string targetName, string fileName, CancellationToken cancellationToken = default);
}

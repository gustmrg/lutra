using System.Text;
using Microsoft.Data.Sqlite;
using Lutra.Core.Persistence;

namespace Lutra.Core.History;

/// <summary>Owns backup-history queries in Lutra's application database.</summary>
public sealed class SqliteBackupHistoryRepository : IBackupHistoryService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly LutraDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteBackupHistoryRepository(LutraDatabase database, TimeProvider? timeProvider = null)
    {
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _database.Initialize();
    }

    public Task<HistoryOperationLease> BeginOperationAsync(
        string targetName,
        HistoryOperationType operationType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        var now = _timeProvider.GetUtcNow();
        var operationId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        using var connection = _database.OpenConnection(cancellationToken);
        var transactionStarted = false;
        try
        {
            ExecuteNonQuery(connection, "BEGIN IMMEDIATE;", cancellationToken);
            transactionStarted = true;
            InterruptExpiredOperations(connection, now, cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO backup_operations (
                    id, target_name, operation_type, status,
                    started_at_unix_ms, updated_at_unix_ms, completed_at_unix_ms,
                    lease_id, lease_expires_at_unix_ms,
                    file_name, file_size_bytes, sha256, manifest_file_name,
                    duration_ms, error_message)
                VALUES (
                    $id, $targetName, $operationType, $status,
                    $now, $now, NULL,
                    $leaseId, $leaseExpiresAt,
                    NULL, NULL, NULL, NULL, NULL, NULL);
                """;
            AddParameter(command, "$id", operationId.ToString("N"));
            AddParameter(command, "$targetName", targetName);
            AddParameter(command, "$operationType", ToDatabaseValue(operationType));
            AddParameter(command, "$status", ToDatabaseValue(ActiveStatusFor(operationType)));
            AddParameter(command, "$now", now.ToUnixTimeMilliseconds());
            AddParameter(command, "$leaseId", leaseId.ToString("N"));
            AddParameter(command, "$leaseExpiresAt", now.Add(LeaseDuration).ToUnixTimeMilliseconds());
            cancellationToken.ThrowIfCancellationRequested();
            command.ExecuteNonQuery();
            ExecuteNonQuery(connection, "COMMIT;", cancellationToken);
            transactionStarted = false;
            return Task.FromResult(new HistoryOperationLease(operationId, leaseId));
        }
        catch
        {
            if (transactionStarted)
                TryRollback(connection);
            throw;
        }
    }

    public Task CompleteOperationAsync(
        Guid operationId,
        Guid leaseId,
        HistoryOperationCompletion completion,
        CancellationToken cancellationToken = default)
    {
        if (completion.FileSizeBytes < 0 || completion.DurationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(completion));
        return TransitionTerminalAsync(
            operationId,
            leaseId,
            HistoryOperationStatus.Succeeded,
            completion,
            errorMessage: null,
            cancellationToken);
    }

    public Task FailOperationAsync(
        Guid operationId,
        Guid leaseId,
        string errorMessage,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return TransitionTerminalAsync(
            operationId,
            leaseId,
            HistoryOperationStatus.Failed,
            new HistoryOperationCompletion(DurationMs: durationMs),
            errorMessage,
            cancellationToken);
    }

    public Task CancelOperationAsync(
        Guid operationId,
        Guid leaseId,
        string? errorMessage = null,
        long? durationMs = null,
        CancellationToken cancellationToken = default)
        => TransitionTerminalAsync(
            operationId,
            leaseId,
            HistoryOperationStatus.Cancelled,
            new HistoryOperationCompletion(DurationMs: durationMs),
            errorMessage ?? "Operation was cancelled.",
            cancellationToken);

    public Task InterruptOperationAsync(
        Guid operationId,
        Guid leaseId,
        string errorMessage,
        CancellationToken cancellationToken = default)
        => TransitionTerminalAsync(
            operationId,
            leaseId,
            HistoryOperationStatus.Interrupted,
            new HistoryOperationCompletion(),
            errorMessage,
            cancellationToken);

    public Task RenewLeaseAsync(
        Guid operationId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        using var connection = _database.OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_operations
            SET updated_at_unix_ms = $now,
                lease_expires_at_unix_ms = $leaseExpiresAt
            WHERE id = $id
              AND lease_id = $leaseId
              AND lease_expires_at_unix_ms > $now
              AND status IN ('creating', 'verifying', 'uploading');
            """;
        AddParameter(command, "$now", now.ToUnixTimeMilliseconds());
        AddParameter(command, "$leaseExpiresAt", now.Add(LeaseDuration).ToUnixTimeMilliseconds());
        AddParameter(command, "$id", operationId.ToString("N"));
        AddParameter(command, "$leaseId", leaseId.ToString("N"));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLeaseOwned(command.ExecuteNonQuery(), operationId, leaseId);
        return Task.CompletedTask;
    }

    public Task AddRecordAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        ValidateTerminalRecord(record);
        using var connection = _database.OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backup_operations (
                id, target_name, operation_type, status,
                started_at_unix_ms, updated_at_unix_ms, completed_at_unix_ms,
                lease_id, lease_expires_at_unix_ms,
                file_name, file_size_bytes, sha256, manifest_file_name,
                duration_ms, error_message)
            VALUES (
                $id, $targetName, $operationType, $status,
                $startedAt, $updatedAt, $completedAt,
                NULL, NULL,
                $fileName, $fileSizeBytes, $sha256, $manifestFileName,
                $durationMs, $errorMessage);
            """;
        AddParameter(command, "$id", record.Id.ToString("N"));
        AddParameter(command, "$targetName", record.TargetName);
        AddParameter(command, "$operationType", ToDatabaseValue(record.OperationType));
        AddParameter(command, "$status", ToDatabaseValue(record.Status));
        AddParameter(command, "$startedAt", record.StartedAt.ToUnixTimeMilliseconds());
        AddParameter(command, "$updatedAt", record.UpdatedAt.ToUnixTimeMilliseconds());
        AddParameter(command, "$completedAt", record.CompletedAt!.Value.ToUnixTimeMilliseconds());
        AddParameter(command, "$fileName", record.FileName);
        AddParameter(command, "$fileSizeBytes", record.FileSizeBytes);
        AddParameter(command, "$sha256", record.Sha256);
        AddParameter(command, "$manifestFileName", record.ManifestFileName);
        AddParameter(command, "$durationMs", record.DurationMs);
        AddParameter(command, "$errorMessage", record.ErrorMessage);
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryRecord>> GetAllRecordsAsync(
        CancellationToken cancellationToken = default)
        => GetRecordsAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<HistoryRecord>> GetRecordsByTargetAsync(
        string targetName,
        CancellationToken cancellationToken = default)
        => GetRecordsAsync(targetName, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<HistoryRecord>> GetRecordsAsync(
        string? targetName = null,
        HistoryOperationType? operationType = null,
        HistoryOperationStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = _database.OpenConnection(cancellationToken);
        InterruptExpiredOperations(connection, _timeProvider.GetUtcNow(), cancellationToken);
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT id, target_name, operation_type, status,
                   started_at_unix_ms, updated_at_unix_ms, completed_at_unix_ms,
                   lease_id, lease_expires_at_unix_ms,
                   file_name, file_size_bytes, sha256, manifest_file_name,
                   duration_ms, error_message
            FROM backup_operations
            """);
        var filters = new List<string>();
        if (targetName is not null)
        {
            filters.Add("target_name = $targetName");
            AddParameter(command, "$targetName", targetName);
        }
        if (operationType is not null)
        {
            filters.Add("operation_type = $operationType");
            AddParameter(command, "$operationType", ToDatabaseValue(operationType.Value));
        }
        if (status is not null)
        {
            filters.Add("status = $status");
            AddParameter(command, "$status", ToDatabaseValue(status.Value));
        }
        if (filters.Count > 0)
            sql.Append(" WHERE ").AppendJoin(" AND ", filters);
        sql.Append(" ORDER BY started_at_unix_ms DESC, id DESC;");
        command.CommandText = sql.ToString();

        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        var records = new List<HistoryRecord>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(ReadRecord(reader));
        }
        return Task.FromResult<IReadOnlyList<HistoryRecord>>(records);
    }

    public Task<bool> RemoveRecordAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = _database.OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM backup_operations
            WHERE id = $id
              AND operation_type = 'backup'
              AND status = 'succeeded';
            """;
        AddParameter(command, "$id", id.ToString("N"));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(command.ExecuteNonQuery() == 1);
    }

    public Task<bool> UpdateRecordAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        ValidateTerminalRecord(record);
        using var connection = _database.OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_operations
            SET target_name = $targetName,
                operation_type = $operationType,
                status = $status,
                started_at_unix_ms = $startedAt,
                updated_at_unix_ms = $updatedAt,
                completed_at_unix_ms = $completedAt,
                lease_id = NULL,
                lease_expires_at_unix_ms = NULL,
                file_name = $fileName,
                file_size_bytes = $fileSizeBytes,
                sha256 = $sha256,
                manifest_file_name = $manifestFileName,
                duration_ms = $durationMs,
                error_message = $errorMessage
            WHERE id = $id
              AND status IN ('succeeded', 'failed', 'cancelled', 'interrupted');
            """;
        AddParameter(command, "$id", record.Id.ToString("N"));
        AddParameter(command, "$targetName", record.TargetName);
        AddParameter(command, "$operationType", ToDatabaseValue(record.OperationType));
        AddParameter(command, "$status", ToDatabaseValue(record.Status));
        AddParameter(command, "$startedAt", record.StartedAt.ToUnixTimeMilliseconds());
        AddParameter(command, "$updatedAt", record.UpdatedAt.ToUnixTimeMilliseconds());
        AddParameter(command, "$completedAt", record.CompletedAt!.Value.ToUnixTimeMilliseconds());
        AddParameter(command, "$fileName", record.FileName);
        AddParameter(command, "$fileSizeBytes", record.FileSizeBytes);
        AddParameter(command, "$sha256", record.Sha256);
        AddParameter(command, "$manifestFileName", record.ManifestFileName);
        AddParameter(command, "$durationMs", record.DurationMs);
        AddParameter(command, "$errorMessage", record.ErrorMessage);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(command.ExecuteNonQuery() == 1);
    }

    public Task<int> PruneOperationalRecordsAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        using var connection = _database.OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM backup_operations
            WHERE started_at_unix_ms < $olderThan
              AND status IN ('succeeded', 'failed', 'cancelled', 'interrupted')
              AND (operation_type <> 'backup' OR status <> 'succeeded');
            """;
        AddParameter(command, "$olderThan", olderThan.ToUnixTimeMilliseconds());
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(command.ExecuteNonQuery());
    }

    private static void ValidateTerminalRecord(HistoryRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.TargetName);
        if (record.Status is HistoryOperationStatus.Creating
            or HistoryOperationStatus.Verifying
            or HistoryOperationStatus.Uploading)
            throw new ArgumentException("Terminal CRUD cannot insert an active history status.", nameof(record));
        if (record.CompletedAt is null)
            throw new ArgumentException("A terminal history record requires CompletedAt.", nameof(record));
        if (record.LeaseId is not null || record.LeaseExpiresAt is not null)
            throw new ArgumentException("A terminal history record cannot retain a lease.", nameof(record));
        if (record.FileSizeBytes < 0 || record.DurationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(record), "Sizes and durations cannot be negative.");
    }

    private Task TransitionTerminalAsync(
        Guid operationId,
        Guid leaseId,
        HistoryOperationStatus status,
        HistoryOperationCompletion completion,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (!status.IsTerminal())
            throw new ArgumentException("A terminal transition requires a terminal status.", nameof(status));
        if (completion.DurationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(completion));

        var now = _timeProvider.GetUtcNow();
        using var connection = _database.OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_operations
            SET status = $status,
                updated_at_unix_ms = $now,
                completed_at_unix_ms = $now,
                lease_id = NULL,
                lease_expires_at_unix_ms = NULL,
                file_name = $fileName,
                file_size_bytes = $fileSizeBytes,
                sha256 = $sha256,
                manifest_file_name = $manifestFileName,
                duration_ms = COALESCE(
                    $durationMs,
                    MAX(0, $now - started_at_unix_ms)),
                error_message = $errorMessage
            WHERE id = $id
              AND lease_id = $leaseId
              AND lease_expires_at_unix_ms > $now
              AND status IN ('creating', 'verifying', 'uploading');
            """;
        AddParameter(command, "$status", ToDatabaseValue(status));
        AddParameter(command, "$now", now.ToUnixTimeMilliseconds());
        AddParameter(command, "$fileName", completion.FileName);
        AddParameter(command, "$fileSizeBytes", completion.FileSizeBytes);
        AddParameter(command, "$sha256", completion.Sha256);
        AddParameter(command, "$manifestFileName", completion.ManifestFileName);
        AddParameter(command, "$durationMs", completion.DurationMs);
        AddParameter(command, "$errorMessage", errorMessage);
        AddParameter(command, "$id", operationId.ToString("N"));
        AddParameter(command, "$leaseId", leaseId.ToString("N"));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLeaseOwned(command.ExecuteNonQuery(), operationId, leaseId);
        return Task.CompletedTask;
    }

    private static void InterruptExpiredOperations(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_operations
            SET status = 'interrupted',
                updated_at_unix_ms = $now,
                completed_at_unix_ms = $now,
                lease_id = NULL,
                lease_expires_at_unix_ms = NULL,
                duration_ms = MAX(0, $now - started_at_unix_ms),
                error_message = COALESCE(error_message, 'Operation lease expired.')
            WHERE status IN ('creating', 'verifying', 'uploading')
              AND lease_expires_at_unix_ms <= $now;
            """;
        AddParameter(command, "$now", now.ToUnixTimeMilliseconds());
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
    }

    private static HistoryOperationStatus ActiveStatusFor(HistoryOperationType operationType)
        => operationType switch
        {
            HistoryOperationType.Backup => HistoryOperationStatus.Creating,
            HistoryOperationType.Verify => HistoryOperationStatus.Verifying,
            HistoryOperationType.Sync => HistoryOperationStatus.Uploading,
            _ => throw new ArgumentOutOfRangeException(nameof(operationType))
        };

    private static void EnsureLeaseOwned(int affectedRows, Guid operationId, Guid leaseId)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId:N}' is no longer active or lease '{leaseId:N}' is not its owner.");
        }
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }

    private static HistoryRecord ReadRecord(SqliteDataReader reader)
    {
        return new HistoryRecord
        {
            Id = Guid.ParseExact(reader.GetString(0), "N"),
            TargetName = reader.GetString(1),
            OperationType = ParseOperationType(reader.GetString(2)),
            Status = ParseStatus(reader.GetString(3)),
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
            CompletedAt = ReadDateTimeOffset(reader, 6),
            LeaseId = reader.IsDBNull(7) ? null : Guid.ParseExact(reader.GetString(7), "N"),
            LeaseExpiresAt = ReadDateTimeOffset(reader, 8),
            FileName = reader.IsDBNull(9) ? null : reader.GetString(9),
            FileSizeBytes = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            Sha256 = reader.IsDBNull(11) ? null : reader.GetString(11),
            ManifestFileName = reader.IsDBNull(12) ? null : reader.GetString(12),
            DurationMs = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    private static void AddParameter(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string ToDatabaseValue(HistoryOperationType value) => value switch
    {
        HistoryOperationType.Backup => "backup",
        HistoryOperationType.Verify => "verify",
        HistoryOperationType.Sync => "sync",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ToDatabaseValue(HistoryOperationStatus value) => value switch
    {
        HistoryOperationStatus.Creating => "creating",
        HistoryOperationStatus.Verifying => "verifying",
        HistoryOperationStatus.Uploading => "uploading",
        HistoryOperationStatus.Succeeded => "succeeded",
        HistoryOperationStatus.Failed => "failed",
        HistoryOperationStatus.Cancelled => "cancelled",
        HistoryOperationStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static HistoryOperationType ParseOperationType(string value) => value switch
    {
        "backup" => HistoryOperationType.Backup,
        "verify" => HistoryOperationType.Verify,
        "sync" => HistoryOperationType.Sync,
        _ => throw new InvalidDataException($"Unknown history operation type '{value}'.")
    };

    private static HistoryOperationStatus ParseStatus(string value) => value switch
    {
        "creating" => HistoryOperationStatus.Creating,
        "verifying" => HistoryOperationStatus.Verifying,
        "uploading" => HistoryOperationStatus.Uploading,
        "succeeded" => HistoryOperationStatus.Succeeded,
        "failed" => HistoryOperationStatus.Failed,
        "cancelled" => HistoryOperationStatus.Cancelled,
        "interrupted" => HistoryOperationStatus.Interrupted,
        _ => throw new InvalidDataException($"Unknown history operation status '{value}'.")
    };
}

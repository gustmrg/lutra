using System.Text;
using Microsoft.Data.Sqlite;
using Lutra.Core.Persistence;

namespace Lutra.Core.History;

/// <summary>Owns backup-history queries in Lutra's application database.</summary>
public sealed class SqliteBackupHistoryRepository : IBackupHistoryService
{
    private readonly LutraDatabase _database;

    public SqliteBackupHistoryRepository(LutraDatabase database)
    {
        _database = database;
        _database.Initialize();
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
        command.CommandText = "DELETE FROM backup_operations WHERE id = $id;";
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
            WHERE id = $id;
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

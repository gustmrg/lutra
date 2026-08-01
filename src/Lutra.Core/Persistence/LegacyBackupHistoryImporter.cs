using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Lutra.Core.Persistence;

internal static class LegacyBackupHistoryImporter
{
    private const string ImportMarker = "legacy.backup_history_json.imported";

    public static void ImportIfNeeded(
        SqliteConnection connection,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var historyPath = Path.Combine(backupDirectory, "backup-history.json");
        if (HasImportMarker(connection, cancellationToken) || !File.Exists(historyPath))
            return;

        if (CountOperations(connection, cancellationToken) != 0)
        {
            throw new InvalidDataException(
                $"Cannot import legacy history '{historyPath}': backup_operations is nonempty " +
                "but the import marker is missing.");
        }

        List<LegacyBackupRecord> records;
        try
        {
            var bytes = File.ReadAllBytes(historyPath);
            cancellationToken.ThrowIfCancellationRequested();
            records = JsonSerializer.Deserialize<List<LegacyBackupRecord>>(bytes)
                ?? throw new JsonException("The JSON root must be an array.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Cannot import legacy history '{historyPath}': {ex.Message}", ex);
        }

        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                InsertRecord(connection, records[index], index, historyPath, cancellationToken);
            }
            catch (Exception ex) when (ex is SqliteException or ArgumentException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException(
                    $"Cannot import legacy history '{historyPath}' record [{index}]: {ex.Message}", ex);
            }
        }

        using var marker = connection.CreateCommand();
        marker.CommandText = """
            INSERT INTO app_metadata (key, value)
            VALUES ($key, $value);
            """;
        marker.Parameters.AddWithValue("$key", ImportMarker);
        marker.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O"));
        cancellationToken.ThrowIfCancellationRequested();
        marker.ExecuteNonQuery();
    }

    private static void InsertRecord(
        SqliteConnection connection,
        LegacyBackupRecord record,
        int index,
        string historyPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.TargetName))
            throw new ArgumentException("target_name is required.");
        if (record.FileSizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(record.FileSizeBytes), "file_size_bytes cannot be negative.");
        if (record.DurationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(record.DurationMs), "duration_ms cannot be negative.");

        var operationType = record.RecordType switch
        {
            null => "backup",
            "verify" => "verify",
            "sync" => "sync",
            _ => throw new ArgumentException(
                $"unknown record_type '{record.RecordType}' in '{historyPath}' record [{index}].")
        };
        var knownInstant = NormalizeUtc(record.Timestamp);
        var duration = TimeSpan.FromMilliseconds(record.DurationMs);
        var startedAt = operationType == "verify" ? knownInstant - duration : knownInstant;
        var completedAt = operationType switch
        {
            "backup" => knownInstant + duration,
            "verify" => knownInstant,
            _ => knownInstant
        };

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
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$targetName", record.TargetName);
        command.Parameters.AddWithValue("$operationType", operationType);
        command.Parameters.AddWithValue("$status", record.Success ? "succeeded" : "failed");
        command.Parameters.AddWithValue("$startedAt", startedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updatedAt", completedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$completedAt", completedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$fileName", EmptyToNull(record.FileName));
        command.Parameters.AddWithValue("$fileSizeBytes", record.FileSizeBytes);
        command.Parameters.AddWithValue("$sha256", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$manifestFileName", (object?)record.ManifestFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", record.DurationMs);
        command.Parameters.AddWithValue("$errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
        cancellationToken.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
    }

    private static bool HasImportMarker(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM app_metadata WHERE key = $key);";
        command.Parameters.AddWithValue("$key", ImportMarker);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static long CountOperations(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM backup_operations;";
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static DateTimeOffset NormalizeUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    private static object EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private sealed class LegacyBackupRecord
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
        public string? Sha256 { get; init; }

        [JsonPropertyName("manifest_file_name")]
        public string? ManifestFileName { get; init; }

        [JsonPropertyName("duration_ms")]
        public required long DurationMs { get; init; }

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("record_type")]
        public string? RecordType { get; init; }
    }
}

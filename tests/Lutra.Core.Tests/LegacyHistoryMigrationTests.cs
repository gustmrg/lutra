using System.Text;
using System.Text.Json;
using Lutra.Core.History;
using Lutra.Core.Persistence;

namespace Lutra.Core.Tests;

public sealed class LegacyHistoryMigrationTests
{
    [Fact]
    public async Task Initialize_ImportsAllLegacyTypesOnceAndPreservesJsonBytes()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var historyPath = Path.Combine(backupDirectory, "backup-history.json");
        var known = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var json = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            Legacy("target", known, "same.bak", 10, 1000, true, null, "abc", "same.bak.json"),
            Legacy("target", known, "same.bak", 10, 2000, false, "verify", error: "bad restore"),
            Legacy("target", known, "", 0, 3000, true, "sync")
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(historyPath, json);
        var database = CreateDatabase(temp, backupDirectory);

        database.Initialize();
        database.Initialize();

        var repository = new SqliteBackupHistoryRepository(database);
        var records = await repository.GetAllRecordsAsync();
        var backup = Assert.Single(records, record => record.OperationType == HistoryOperationType.Backup);
        var verify = Assert.Single(records, record => record.OperationType == HistoryOperationType.Verify);
        var sync = Assert.Single(records, record => record.OperationType == HistoryOperationType.Sync);
        Assert.Equal(3, records.Count);
        Assert.Equal(new DateTimeOffset(known), backup.StartedAt);
        Assert.Equal(new DateTimeOffset(known).AddSeconds(1), backup.CompletedAt);
        Assert.Equal(new DateTimeOffset(known).AddSeconds(-2), verify.StartedAt);
        Assert.Equal(new DateTimeOffset(known), verify.CompletedAt);
        Assert.Equal(new DateTimeOffset(known), sync.StartedAt);
        Assert.Equal(sync.StartedAt, sync.CompletedAt);
        Assert.Equal(3000, sync.DurationMs);
        Assert.Null(sync.FileName);
        Assert.Equal(HistoryOperationStatus.Failed, verify.Status);
        Assert.Equal("abc", backup.Sha256);
        Assert.Equal("same.bak.json", backup.ManifestFileName);
        Assert.True(HasImportMarker(database));
        Assert.Equal(json, await File.ReadAllBytesAsync(historyPath));
    }

    [Fact]
    public async Task Initialize_ConcurrentImportOccursExactlyOnce()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        await WriteLegacyAsync(backupDirectory,
            Legacy("target", DateTime.UtcNow, "one.bak", 1, 1, true),
            Legacy("target", DateTime.UtcNow, "two.bak", 2, 2, true));
        var first = CreateDatabase(temp, backupDirectory);
        var second = CreateDatabase(temp, backupDirectory);

        await Task.WhenAll(Task.Run(() => first.Initialize()), Task.Run(() => second.Initialize()));

        Assert.Equal(2, (await new SqliteBackupHistoryRepository(first).GetAllRecordsAsync()).Count);
        Assert.True(HasImportMarker(first));
    }

    [Fact]
    public void Initialize_WithNoJsonCreatesDatabaseWithoutImportMarker()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var database = CreateDatabase(temp, backupDirectory);

        database.Initialize();

        Assert.False(HasImportMarker(database));
        Assert.Equal(0, CountOperations(database));
    }

    [Fact]
    public async Task Initialize_InvalidJsonRollsBackAndLeavesOriginalBytes()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var database = CreateDatabase(temp, backupDirectory);
        database.Initialize();
        var historyPath = Path.Combine(backupDirectory, "backup-history.json");
        var invalid = Encoding.UTF8.GetBytes("[{ definitely-not-json ]");
        await File.WriteAllBytesAsync(historyPath, invalid);

        var error = Assert.Throws<InvalidDataException>(() => database.Initialize());

        Assert.Contains(historyPath, error.Message);
        Assert.Equal(0, CountOperations(database));
        Assert.False(HasImportMarker(database));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(historyPath));
    }

    [Fact]
    public async Task Initialize_UnknownTypeRollsBackEarlierRows()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var database = CreateDatabase(temp, backupDirectory);
        database.Initialize();
        await WriteLegacyAsync(backupDirectory,
            Legacy("target", DateTime.UtcNow, "valid.bak", 1, 1, true),
            Legacy("target", DateTime.UtcNow, "unknown.bak", 1, 1, true, "export"));

        var error = Assert.Throws<InvalidDataException>(() => database.Initialize());

        Assert.Contains("record [1]", error.Message);
        Assert.Contains("unknown record_type 'export'", error.Message);
        Assert.Equal(0, CountOperations(database));
        Assert.False(HasImportMarker(database));
    }

    [Fact]
    public async Task Initialize_NonemptyUnmarkedDatabaseRefusesImportWithoutChangingRows()
    {
        using var temp = new TempDirectory();
        var backupDirectory = Path.Combine(temp.Path, "backups");
        Directory.CreateDirectory(backupDirectory);
        var database = CreateDatabase(temp, backupDirectory);
        var repository = new SqliteBackupHistoryRepository(database);
        var timestamp = DateTimeOffset.UtcNow;
        await repository.AddRecordAsync(new HistoryRecord
        {
            TargetName = "existing",
            OperationType = HistoryOperationType.Backup,
            Status = HistoryOperationStatus.Succeeded,
            StartedAt = timestamp,
            UpdatedAt = timestamp,
            CompletedAt = timestamp,
            FileName = "existing.bak",
            FileSizeBytes = 1,
            DurationMs = 0
        });
        await WriteLegacyAsync(backupDirectory,
            Legacy("legacy", DateTime.UtcNow, "legacy.bak", 1, 1, true));

        var error = Assert.Throws<InvalidDataException>(() => database.Initialize());

        Assert.Contains("backup_operations is nonempty", error.Message);
        Assert.Equal(1, CountOperations(database));
        Assert.False(HasImportMarker(database));
    }

    private static LutraDatabase CreateDatabase(TempDirectory temp, string backupDirectory)
        => new(
            Path.Combine(temp.Path, "state"),
            Path.Combine(temp.Path, "lutra.yaml"),
            backupDirectory);

    private static async Task WriteLegacyAsync(string backupDirectory, params object[] records)
        => await File.WriteAllBytesAsync(
            Path.Combine(backupDirectory, "backup-history.json"),
            JsonSerializer.SerializeToUtf8Bytes(records, new JsonSerializerOptions { WriteIndented = true }));

    private static object Legacy(
        string target,
        DateTime timestamp,
        string fileName,
        long fileSize,
        long duration,
        bool success,
        string? recordType = null,
        string? sha256 = null,
        string? manifest = null,
        string? error = null)
        => new
        {
            target_name = target,
            timestamp,
            file_name = fileName,
            file_size_bytes = fileSize,
            sha256,
            manifest_file_name = manifest,
            duration_ms = duration,
            success,
            error_message = error,
            record_type = recordType
        };

    private static bool HasImportMarker(LutraDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM app_metadata
                WHERE key = 'legacy.backup_history_json.imported');
            """;
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static long CountOperations(LutraDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM backup_operations;";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

using Lutra.Core.History;
using Lutra.Core.Persistence;

namespace Lutra.Core.Tests;

public sealed class SqliteHistoryRepositoryTests
{
    [Fact]
    public void Initialize_AppliesOrderedMigrationsOnceAndChecksIntegrity()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabase(temp);

        database.Initialize();
        database.Initialize();

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version || ':' || name FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        var ledger = new List<string>();
        while (reader.Read())
            ledger.Add(reader.GetString(0));

        Assert.Equal(
            ["1:001_application_database", "2:002_backup_operations"],
            ledger);
        Assert.Equal("ok", database.CheckIntegrity());
        Assert.True(File.Exists(Path.Combine(temp.Path, "state", "lutra.db")));

        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "SELECT (SELECT * FROM pragma_journal_mode), " +
                                    "(SELECT * FROM pragma_synchronous), " +
                                    "(SELECT * FROM pragma_foreign_keys), " +
                                    "(SELECT * FROM pragma_busy_timeout);";
        using var pragmaReader = pragmaCommand.ExecuteReader();
        Assert.True(pragmaReader.Read());
        Assert.Equal("wal", pragmaReader.GetString(0));
        Assert.Equal(2, pragmaReader.GetInt32(1));
        Assert.Equal(1, pragmaReader.GetInt32(2));
        Assert.Equal(30000, pragmaReader.GetInt32(3));
    }

    [Fact]
    public void Initialize_RejectsAStateDirectoryOwnedByAnotherConfig()
    {
        using var temp = new TempDirectory();
        var stateDirectory = Path.Combine(temp.Path, "state");
        var firstPath = Path.Combine(temp.Path, "one", "..", "lutra.yaml");
        var equivalentPath = Path.Combine(temp.Path, "lutra.yaml");
        var otherPath = Path.Combine(temp.Path, "other.yaml");

        new LutraDatabase(stateDirectory, firstPath).Initialize();
        new LutraDatabase(stateDirectory, equivalentPath).Initialize();
        var error = Assert.Throws<InvalidOperationException>(
            () => new LutraDatabase(stateDirectory, otherPath).Initialize());

        Assert.Contains("Select a distinct state_directory", error.Message);
    }

    [Fact]
    public async Task ConcurrentInserts_FromSeparateRepositories_PreserveExactRecordsAndFilters()
    {
        using var temp = new TempDirectory();
        var repositories = Enumerable.Range(0, 8)
            .Select(_ => new SqliteBackupHistoryRepository(CreateDatabase(temp)))
            .ToArray();
        var timestamp = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(ids.Select((id, index) => Task.Run(() =>
            repositories[index % repositories.Length].AddRecordAsync(new HistoryRecord
            {
                Id = id,
                TargetName = index % 2 == 0 ? "even" : "odd",
                OperationType = HistoryOperationType.Backup,
                Status = HistoryOperationStatus.Succeeded,
                StartedAt = timestamp,
                UpdatedAt = timestamp.AddMilliseconds(1),
                CompletedAt = timestamp.AddMilliseconds(2),
                FileName = $"{index}.bak",
                FileSizeBytes = index,
                DurationMs = 2
            }))));

        var all = await repositories[0].GetAllRecordsAsync();
        var even = await repositories[1].GetRecordsByTargetAsync("even");

        Assert.Equal(100, all.Count);
        Assert.Equal(50, even.Count);
        Assert.Equal(ids.OrderByDescending(id => id.ToString("N")), all.Select(record => record.Id));
        Assert.Equal(ids.OrderBy(id => id), all.Select(record => record.Id).OrderBy(id => id));
        Assert.Equal("ok", CreateDatabase(temp).CheckIntegrity());
    }

    [Fact]
    public async Task TerminalCrud_FiltersRemovesByIdAndPrunesOnlyOperationalRows()
    {
        using var temp = new TempDirectory();
        var repository = new SqliteBackupHistoryRepository(CreateDatabase(temp));
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var recent = DateTimeOffset.UtcNow;
        var successfulBackup = TerminalRecord("target", HistoryOperationType.Backup, HistoryOperationStatus.Succeeded, old);
        var failedBackup = TerminalRecord("target", HistoryOperationType.Backup, HistoryOperationStatus.Failed, old);
        var verification = TerminalRecord("target", HistoryOperationType.Verify, HistoryOperationStatus.Succeeded, old);
        var otherTarget = TerminalRecord("other", HistoryOperationType.Sync, HistoryOperationStatus.Failed, recent);

        foreach (var record in new[] { successfulBackup, failedBackup, verification, otherTarget })
            await repository.AddRecordAsync(record);

        var updatedOtherTarget = new HistoryRecord
        {
            Id = otherTarget.Id,
            TargetName = otherTarget.TargetName,
            OperationType = otherTarget.OperationType,
            Status = HistoryOperationStatus.Succeeded,
            StartedAt = otherTarget.StartedAt,
            UpdatedAt = otherTarget.UpdatedAt.AddSeconds(1),
            CompletedAt = otherTarget.CompletedAt,
            DurationMs = otherTarget.DurationMs
        };
        Assert.True(await repository.UpdateRecordAsync(updatedOtherTarget));

        var failures = await repository.GetRecordsAsync(status: HistoryOperationStatus.Failed);
        Assert.Single(failures);
        Assert.Equal(3, (await repository.GetRecordsByTargetAsync("target")).Count);
        Assert.True(await repository.RemoveRecordAsync(otherTarget.Id));
        Assert.False(await repository.RemoveRecordAsync(otherTarget.Id));
        Assert.Equal(2, await repository.PruneOperationalRecordsAsync(DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal([successfulBackup.Id], (await repository.GetAllRecordsAsync()).Select(record => record.Id));
    }

    private static LutraDatabase CreateDatabase(TempDirectory temp)
        => new(Path.Combine(temp.Path, "state"), Path.Combine(temp.Path, "lutra.yaml"));

    private static HistoryRecord TerminalRecord(
        string target,
        HistoryOperationType type,
        HistoryOperationStatus status,
        DateTimeOffset timestamp)
        => new()
        {
            TargetName = target,
            OperationType = type,
            Status = status,
            StartedAt = timestamp,
            UpdatedAt = timestamp.AddSeconds(1),
            CompletedAt = timestamp.AddSeconds(1),
            DurationMs = 1000
        };
}

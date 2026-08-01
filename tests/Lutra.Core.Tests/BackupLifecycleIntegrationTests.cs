using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;
using Lutra.Core.Persistence;
using Lutra.Core.Restore;

namespace Lutra.Core.Tests;

public sealed class BackupLifecycleIntegrationTests
{
    [Fact]
    public async Task Backup_ExposesCreatingThenCompletesSucceeded()
    {
        using var temp = new TempDirectory();
        var (config, target, history) = CreateDatabaseScenario(temp);
        var executor = new BlockingProcessExecutor();
        var orchestrator = new BackupOrchestrator(
            [new StreamingTestProvider()], executor, history, config);

        var backupTask = orchestrator.BackupAsync(target);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var active = Assert.Single(await history.GetAllRecordsAsync());
        Assert.Equal(HistoryOperationStatus.Creating, active.Status);
        Assert.NotNull(active.LeaseId);
        executor.Release.TrySetResult();

        var result = await backupTask;
        var completed = Assert.Single(await history.GetAllRecordsAsync());
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(HistoryOperationStatus.Succeeded, completed.Status);
        Assert.NotNull(completed.FileName);
        Assert.Null(completed.LeaseId);
    }

    [Fact]
    public async Task CancelledBackup_PersistsCancelledAndCleansTemporaryArtifact()
    {
        using var temp = new TempDirectory();
        var (config, target, history) = CreateDatabaseScenario(temp);
        var executor = new BlockingProcessExecutor();
        var orchestrator = new BackupOrchestrator(
            [new StreamingTestProvider()], executor, history, config);
        using var cancellation = new CancellationTokenSource();

        var backupTask = orchestrator.BackupAsync(target, cancellation.Token);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        var result = await backupTask;
        var record = Assert.Single(await history.GetAllRecordsAsync());
        Assert.False(result.Success);
        Assert.Equal(HistoryOperationStatus.Cancelled, record.Status);
        var targetDirectory = Path.Combine(config.BackupDirectory, target.Name);
        Assert.DoesNotContain(
            Directory.Exists(targetDirectory) ? Directory.EnumerateFiles(targetDirectory) : [],
            path => Path.GetFileName(path).StartsWith('.'));
    }

    [Fact]
    public async Task FinalizedArtifact_SurvivesHistoryCompletionFailureForReconciliation()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "important");
        var target = new FileTarget
        {
            Name = "files",
            Paths = [sourcePath],
            Schedule = "daily",
            Compression = CompressionType.None
        };
        var config = new BackupConfig
        {
            BackupDirectory = Path.Combine(temp.Path, "backups"),
            StateDirectory = Path.Combine(temp.Path, "state"),
            ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
            Retention = new RetentionPolicy(),
            Files = [target]
        };
        var innerHistory = CreateHistory(config);
        var failingHistory = new CompletionFailingHistory(innerHistory);
        var orchestrator = new BackupOrchestrator(
            [], new NeverProcessExecutor(), failingHistory, config);

        var result = await orchestrator.BackupFilesAsync(target);

        Assert.False(result.Success);
        Assert.Contains("Simulated history completion failure", result.ErrorMessage);
        var targetDirectory = Path.Combine(config.BackupDirectory, target.Name);
        var artifact = Assert.Single(Directory.EnumerateFiles(targetDirectory), path =>
            !path.EndsWith(".sha256", StringComparison.Ordinal)
            && !path.EndsWith(".json", StringComparison.Ordinal)
            && !Path.GetFileName(path).StartsWith('.'));
        Assert.True(File.Exists(BackupIntegrity.GetChecksumPath(artifact)));
        Assert.True(File.Exists(BackupIntegrity.GetManifestPath(artifact)));
        var reconciliation = await new BackupReconciliationService(config, innerHistory).ReconcileAsync();
        Assert.Contains(
            reconciliation.Findings,
            finding => finding.Type == ReconciliationFindingType.FileWithoutHistory
                && finding.Path == artifact);
    }

    [Fact]
    public async Task FailedVerification_TransitionsVerifyingRowToFailed()
    {
        using var temp = new TempDirectory();
        var (config, target, history) = CreateDatabaseScenario(temp);
        var orchestrator = new RestoreOrchestrator(
            [], new NeverProcessExecutor(), history, config);

        var result = await orchestrator.TestRestoreAsync(
            target, Path.Combine(temp.Path, "missing.dump"));

        Assert.False(result.Success);
        var record = Assert.Single(await history.GetAllRecordsAsync());
        Assert.Equal(HistoryOperationType.Verify, record.OperationType);
        Assert.Equal(HistoryOperationStatus.Failed, record.Status);
        Assert.Contains("Backup file not found", record.ErrorMessage);
    }

    private static (BackupConfig Config, DatabaseTarget Target, SqliteBackupHistoryRepository History)
        CreateDatabaseScenario(TempDirectory temp)
    {
        var target = new DatabaseTarget
        {
            Name = "database",
            Type = DatabaseType.PostgreSql,
            Container = "postgres",
            Database = "app",
            Username = "postgres",
            Schedule = "daily",
            Compression = CompressionType.None
        };
        var config = new BackupConfig
        {
            BackupDirectory = Path.Combine(temp.Path, "backups"),
            StateDirectory = Path.Combine(temp.Path, "state"),
            ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
            Retention = new RetentionPolicy(),
            Databases = [target]
        };
        return (config, target, CreateHistory(config));
    }

    private static SqliteBackupHistoryRepository CreateHistory(BackupConfig config)
        => new(new LutraDatabase(
            config.StateDirectory!, config.ConfigPath!, config.BackupDirectory));

    private sealed class StreamingTestProvider : IBackupProvider
    {
        public DatabaseType Type => DatabaseType.PostgreSql;

        public DockerExecCommand BuildDumpCommand(DatabaseTarget target, string backupId)
            => new(target.Container, "fake-dump", []);

        public string GetFileExtension(DatabaseTarget target) => ".dump";
    }

    private sealed class BlockingProcessExecutor : IProcessExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessResult> ExecuteAsync(
            DockerExecCommand command,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ProcessResult(0, new MemoryStream("backup"u8.ToArray()), "");
        }

        public Task<ProcessResult> ExecuteWithInputAsync(
            DockerExecCommand command,
            Stream input,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");
    }

    private sealed class NeverProcessExecutor : IProcessExecutor
    {
        public Task<ProcessResult> ExecuteAsync(
            DockerExecCommand command,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");

        public Task<ProcessResult> ExecuteWithInputAsync(
            DockerExecCommand command,
            Stream input,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");
    }

    private sealed class CompletionFailingHistory(IBackupHistoryService inner) : IBackupHistoryService
    {
        public Task<HistoryOperationLease> BeginOperationAsync(string targetName, HistoryOperationType operationType, CancellationToken cancellationToken = default)
            => inner.BeginOperationAsync(targetName, operationType, cancellationToken);

        public Task CompleteOperationAsync(Guid operationId, Guid leaseId, HistoryOperationCompletion completion, CancellationToken cancellationToken = default)
            => Task.FromException(new IOException("Simulated history completion failure."));

        public Task FailOperationAsync(Guid operationId, Guid leaseId, string errorMessage, long? durationMs = null, CancellationToken cancellationToken = default)
            => inner.FailOperationAsync(operationId, leaseId, errorMessage, durationMs, cancellationToken);

        public Task CancelOperationAsync(Guid operationId, Guid leaseId, string? errorMessage = null, long? durationMs = null, CancellationToken cancellationToken = default)
            => inner.CancelOperationAsync(operationId, leaseId, errorMessage, durationMs, cancellationToken);

        public Task RenewLeaseAsync(Guid operationId, Guid leaseId, CancellationToken cancellationToken = default)
            => inner.RenewLeaseAsync(operationId, leaseId, cancellationToken);

        public Task InterruptOperationAsync(Guid operationId, Guid leaseId, string errorMessage, CancellationToken cancellationToken = default)
            => inner.InterruptOperationAsync(operationId, leaseId, errorMessage, cancellationToken);

        public Task AddRecordAsync(HistoryRecord record, CancellationToken cancellationToken = default)
            => inner.AddRecordAsync(record, cancellationToken);

        public Task<IReadOnlyList<HistoryRecord>> GetAllRecordsAsync(CancellationToken cancellationToken = default)
            => inner.GetAllRecordsAsync(cancellationToken);

        public Task<IReadOnlyList<HistoryRecord>> GetRecordsByTargetAsync(string targetName, CancellationToken cancellationToken = default)
            => inner.GetRecordsByTargetAsync(targetName, cancellationToken);

        public Task<bool> RemoveRecordAsync(Guid id, CancellationToken cancellationToken = default)
            => inner.RemoveRecordAsync(id, cancellationToken);

        public Task<int> PruneOperationalRecordsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
            => inner.PruneOperationalRecordsAsync(olderThan, cancellationToken);
    }
}

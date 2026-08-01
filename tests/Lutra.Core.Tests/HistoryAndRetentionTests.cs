using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;
using Lutra.Core.Persistence;

namespace Lutra.Core.Tests;

public sealed class HistoryAndRetentionTests
{
    [Fact]
    public async Task ConcurrentHistoryWrites_DoNotLoseRecords()
    {
        using var temp = new TempDirectory();
        var history = CreateHistory(temp);
        var writes = Enumerable.Range(0, 20).Select(index => history.AddRecordAsync(new HistoryRecord
        {
            TargetName = "target",
            OperationType = HistoryOperationType.Backup,
            Status = HistoryOperationStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(index),
            UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(index + 1),
            CompletedAt = DateTimeOffset.UtcNow.AddSeconds(index + 1),
            FileName = $"{index}.bak",
            FileSizeBytes = index,
            DurationMs = 1
        }));

        await Task.WhenAll(writes);

        Assert.Equal(20, (await history.GetAllRecordsAsync()).Count);
    }

    [Fact]
    public async Task EitherRetention_DeletesCountOverflowWhileKeepingMinimum()
    {
        using var temp = new TempDirectory();
        var target = new FileTarget
        {
            Name = "files",
            Paths = [temp.Path],
            Retention = new RetentionPolicy
            {
                MaxCount = 2,
                MaxAgeDays = 100,
                Mode = RetentionMode.Either,
                KeepAtLeast = 1
            }
        };
        var config = new BackupConfig
        {
            BackupDirectory = temp.Path,
            Retention = new RetentionPolicy(),
            Files = [target]
        };
        var history = CreateHistory(temp);
        for (var index = 0; index < 5; index++)
        {
            var startedAt = DateTimeOffset.UtcNow.AddDays(-index);
            await history.AddRecordAsync(new HistoryRecord
            {
                TargetName = target.Name,
                OperationType = HistoryOperationType.Backup,
                Status = HistoryOperationStatus.Succeeded,
                StartedAt = startedAt,
                UpdatedAt = startedAt.AddMilliseconds(1),
                CompletedAt = startedAt.AddMilliseconds(1),
                FileName = $"backup-{index}.tar",
                FileSizeBytes = 10,
                DurationMs = 1
            });
        }
        var orchestrator = new BackupOrchestrator([], new NeverProcessExecutor(), history, config);

        var candidates = await orchestrator.PreviewCleanupAsync(target);

        Assert.Equal(3, candidates.Count);
        Assert.DoesNotContain(candidates, item => item.Record.FileName == "backup-0.tar");
    }

    private static SqliteBackupHistoryRepository CreateHistory(TempDirectory temp)
        => new(new LutraDatabase(
            Path.Combine(temp.Path, "state"),
            Path.Combine(temp.Path, "lutra.yaml"),
            temp.Path));

    private sealed class NeverProcessExecutor : IProcessExecutor
    {
        public Task<ProcessResult> ExecuteAsync(DockerExecCommand command, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");

        public Task<ProcessResult> ExecuteWithInputAsync(
            DockerExecCommand command, Stream input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");
    }
}

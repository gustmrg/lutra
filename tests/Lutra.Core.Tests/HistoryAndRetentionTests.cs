using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Tests;

public sealed class HistoryAndRetentionTests
{
    [Fact]
    public async Task ConcurrentHistoryWrites_DoNotLoseRecords()
    {
        using var temp = new TempDirectory();
        var history = new BackupHistoryService(temp.Path);
        var writes = Enumerable.Range(0, 20).Select(index => history.AddRecordAsync(new BackupRecord
        {
            TargetName = "target",
            Timestamp = DateTime.UtcNow.AddSeconds(index),
            FileName = $"{index}.bak",
            FileSizeBytes = index,
            DurationMs = 1,
            Success = true
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
        var history = new BackupHistoryService(temp.Path);
        for (var index = 0; index < 5; index++)
        {
            await history.AddRecordAsync(new BackupRecord
            {
                TargetName = target.Name,
                Timestamp = DateTime.UtcNow.AddDays(-index),
                FileName = $"backup-{index}.tar",
                FileSizeBytes = 10,
                DurationMs = 1,
                Success = true
            });
        }
        var orchestrator = new BackupOrchestrator([], new NeverProcessExecutor(), history, config);

        var candidates = await orchestrator.PreviewCleanupAsync(target);

        Assert.Equal(3, candidates.Count);
        Assert.DoesNotContain(candidates, item => item.Record.FileName == "backup-0.tar");
    }

    private sealed class NeverProcessExecutor : IProcessExecutor
    {
        public Task<ProcessResult> ExecuteAsync(DockerExecCommand command, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");

        public Task<ProcessResult> ExecuteWithInputAsync(
            DockerExecCommand command, Stream input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected.");
    }
}

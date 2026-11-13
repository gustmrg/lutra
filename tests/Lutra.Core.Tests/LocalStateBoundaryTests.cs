using Lutra.Core.Configuration;
using Lutra.Core.History;
using Lutra.Core.Persistence;
using Lutra.Core.Sync;

namespace Lutra.Core.Tests;

public sealed class LocalStateBoundaryTests
{
    [Fact]
    public async Task FullRootSync_ExcludesLegacyAndApplicationStateFiles()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp);
        Directory.CreateDirectory(config.BackupDirectory);
        Directory.CreateDirectory(Path.Combine(config.BackupDirectory, "files"));
        var runner = new CapturingRsyncRunner();
        var service = new RsyncService(config, CreateHistory(config), runner);

        var result = await service.SyncAsync(null, dryRun: true, delete: false);

        Assert.True(result.Success);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("rsync", invocation.FileName);
        Assert.Contains("/backup-history.json", invocation.Arguments);
        Assert.Contains("/.backup-history.lock", invocation.Arguments);
        Assert.Contains("/.locks/", invocation.Arguments);
        Assert.Contains("*.tmp", invocation.Arguments);
        Assert.Contains("/lutra.db", invocation.Arguments);
        Assert.Contains("/lutra.db-wal", invocation.Arguments);
        Assert.Contains("/lutra.db-shm", invocation.Arguments);
        Assert.Contains("/.lutra-state/", invocation.Arguments);
        Assert.DoesNotContain(invocation.Arguments, argument => argument.Contains(config.StateDirectory!, StringComparison.Ordinal));
        Assert.Equal(
            config.BackupDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            invocation.Arguments[^2]);
    }

    [Fact]
    public async Task TargetSync_StartsBelowBackupRootAndCannotIncludeLocalState()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp);
        var targetDirectory = Path.Combine(config.BackupDirectory, "files");
        Directory.CreateDirectory(targetDirectory);
        var runner = new CapturingRsyncRunner();
        var service = new RsyncService(config, CreateHistory(config), runner);

        var result = await service.SyncAsync("files", dryRun: true, delete: false);

        Assert.True(result.Success);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(
            targetDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            invocation.Arguments[^2]);
        Assert.DoesNotContain(invocation.Arguments, argument => argument.Contains("lutra.db", StringComparison.Ordinal));
        Assert.DoesNotContain(invocation.Arguments, argument => argument.Contains("backup-history.json", StringComparison.Ordinal));
        Assert.DoesNotContain(invocation.Arguments, argument => argument.Contains(config.StateDirectory!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BusyFullSync_FailsHistoryRowsWithoutLaunchingRsyncAndReleasesLocks()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp);
        config.Files.Add(new FileTarget
        {
            Name = "aaa",
            Paths = [temp.Path],
            Schedule = "daily"
        });
        Directory.CreateDirectory(config.BackupDirectory);
        var history = CreateHistory(config);
        var runner = new CapturingRsyncRunner();
        FileStream? acquired = null;
        var requestedLocks = new List<string>();
        FileStream LockFactory(string _, string targetName, string __)
        {
            requestedLocks.Add(targetName);
            if (targetName == "files")
                throw new InvalidOperationException("Sync for target 'files' is already running.");
            acquired = File.Open(
                Path.Combine(temp.Path, targetName + ".lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            return acquired;
        }
        var service = new RsyncService(config, history, runner, LockFactory);

        var result = await service.SyncAsync(null, dryRun: false, delete: false);

        Assert.False(result.Success);
        Assert.Contains("Retry the sync", result.ErrorMessage);
        Assert.Empty(runner.Invocations);
        Assert.Equal(["aaa", "files"], requestedLocks);
        Assert.NotNull(acquired);
        Assert.True(acquired.SafeFileHandle.IsClosed);
        var rows = await history.GetAllRecordsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(HistoryOperationType.Sync, row.OperationType);
            Assert.Equal(HistoryOperationStatus.Failed, row.Status);
            Assert.Contains("Retry the sync", row.ErrorMessage);
        });
    }

    [Fact]
    public async Task Sync_ExposesUploadingThenCompletesSucceeded()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp);
        Directory.CreateDirectory(Path.Combine(config.BackupDirectory, "files"));
        var history = CreateHistory(config);
        var runner = new BlockingRsyncRunner();
        var service = new RsyncService(config, history, runner);

        var syncTask = service.SyncAsync("files", dryRun: false, delete: false);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var active = Assert.Single(await history.GetAllRecordsAsync());
        Assert.Equal(HistoryOperationStatus.Uploading, active.Status);
        runner.Release.TrySetResult();

        var result = await syncTask;
        var completed = Assert.Single(await history.GetAllRecordsAsync());
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(HistoryOperationStatus.Succeeded, completed.Status);
        Assert.Equal(HistoryOperationType.Sync, completed.OperationType);
    }

    private static BackupConfig CreateConfig(TempDirectory temp)
    {
        var backupDirectory = Path.Combine(temp.Path, "backups");
        return new BackupConfig
        {
            BackupDirectory = backupDirectory,
            StateDirectory = Path.Combine(backupDirectory, ".lutra-state"),
            ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
            Retention = new RetentionPolicy(),
            Files =
            [
                new FileTarget
                {
                    Name = "files",
                    Paths = [temp.Path],
                    Schedule = "daily"
                }
            ],
            Sync = new RsyncConfig
            {
                Type = "rsync",
                Host = "backup.example",
                User = "lutra",
                DestinationPath = "/srv/lutra",
                SshKeyPath = Path.Combine(temp.Path, "key")
            }
        };
    }

    private static SqliteBackupHistoryRepository CreateHistory(BackupConfig config)
        => new(new LutraDatabase(
            config.StateDirectory!,
            config.ConfigPath!));

    private sealed class CapturingRsyncRunner : IRsyncProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<RsyncProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments.ToArray()));
            return Task.FromResult(new RsyncProcessResult(0, "ok", ""));
        }
    }

    private sealed class BlockingRsyncRunner : IRsyncProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RsyncProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new RsyncProcessResult(0, "ok", "");
        }
    }
}

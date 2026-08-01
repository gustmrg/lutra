using System.Formats.Tar;
using System.IO.Compression;
using Lutra.Core.Bundle;
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
    public async Task Bundle_ContainsArtifactsButNoApplicationOrLegacyHistoryState()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp);
        var targetDirectory = Path.Combine(config.BackupDirectory, "files");
        Directory.CreateDirectory(targetDirectory);
        var artifactPath = Path.Combine(targetDirectory, "files.tar");
        await File.WriteAllTextAsync(artifactPath, "backup");
        Directory.CreateDirectory(config.StateDirectory!);
        await File.WriteAllTextAsync(Path.Combine(config.BackupDirectory, "backup-history.json"), "[]");
        var history = CreateHistory(config);
        var timestamp = DateTimeOffset.UtcNow;
        await history.AddRecordAsync(new HistoryRecord
        {
            TargetName = "files",
            OperationType = HistoryOperationType.Backup,
            Status = HistoryOperationStatus.Succeeded,
            StartedAt = timestamp,
            UpdatedAt = timestamp,
            CompletedAt = timestamp,
            FileName = Path.GetFileName(artifactPath),
            FileSizeBytes = new FileInfo(artifactPath).Length,
            DurationMs = 0
        });
        await File.WriteAllTextAsync(config.ConfigPath!, $$"""
            backup_directory: {{config.BackupDirectory}}
            state_directory: {{config.StateDirectory}}
            """);
        var output = Path.Combine(temp.Path, "bundle.tar.gz");

        var result = await new DisasterRecoveryBundleService(config, history).CreateAsync(
            config.ConfigPath!, Path.Combine(temp.Path, ".env"), output, encrypt: false);

        Assert.True(result.Success, result.ErrorMessage);
        var entries = ReadTarEntries(output);
        Assert.Contains("backups/files/files.tar", entries);
        Assert.DoesNotContain(entries, entry => entry.Contains("backup-history.json", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains("lutra.db", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains(".lutra-state", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
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
            config.ConfigPath!,
            config.BackupDirectory));

    private static IReadOnlyList<string> ReadTarEntries(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<string>();
        while (reader.GetNextEntry() is { } entry)
            entries.Add(entry.Name);
        return entries;
    }

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
}

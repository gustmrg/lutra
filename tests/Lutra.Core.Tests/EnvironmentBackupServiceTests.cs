using System.Formats.Tar;
using System.IO.Compression;
using Lutra.Core.Configuration;
using Lutra.Core.History;
using Lutra.Core.Inventory;
using Lutra.Core.Persistence;
using Lutra.Core.Recovery;

namespace Lutra.Core.Tests;

public sealed class EnvironmentBackupServiceTests
{
    [Fact]
    public async Task Backup_CreatesCompleteSetAndExcludesSecrets()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "app.conf"), "enabled=true");
        await File.WriteAllTextAsync(Path.Combine(source, ".env"), "SECRET=sentinel-value");
        var config = CreateConfig(temp, source);
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(config, history, new SuccessfulInventory());

        var result = await service.BackupAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(result.FilePath));
        Assert.True(File.Exists(result.FilePath + ".sha256"));
        Assert.True(File.Exists(result.FilePath + ".json"));
        var manifest = await EnvironmentRecoveryArchive.ValidateAsync(result.FilePath!);
        Assert.Equal("app-files", Assert.Single(manifest.Sources).Name);
        Assert.DoesNotContain("sentinel-value", await File.ReadAllTextAsync(result.FilePath + ".json"));
        Assert.DoesNotContain("sentinel-value", await File.ReadAllTextAsync(result.FilePath + ".sha256"));

        var payloadEntries = await ReadNestedPayloadEntriesAsync(result.FilePath!);
        Assert.Contains(payloadEntries, entry => entry.EndsWith("app.conf", StringComparison.Ordinal));
        Assert.DoesNotContain(payloadEntries, entry => entry.EndsWith(".env", StringComparison.Ordinal));

        var record = Assert.Single(await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName));
        Assert.Equal(HistoryOperationStatus.Succeeded, record.Status);
        Assert.Equal(Path.GetFileName(result.FilePath), record.FileName);
        Assert.DoesNotContain("sentinel-value", string.Join('\n', Directory.GetFiles(
            Path.Combine(config.StateDirectory!, "logs", "environment")).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task Backup_RequiredInventoryFailurePublishesNothingAndSanitizesHistory()
    {
        using var temp = new TempDirectory();
        var source = CreateSource(temp);
        var config = CreateConfig(temp, source);
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(config, history, new FailedInventory("sentinel-value"));

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during inventory collection.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
        var record = Assert.Single(await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName));
        Assert.Equal(HistoryOperationStatus.Failed, record.Status);
        Assert.Equal("inventory_failed", record.ErrorMessage);
        Assert.DoesNotContain("sentinel-value", record.ErrorMessage);
    }

    [Fact]
    public async Task Backup_ArchiveFailureLeavesPriorSetAndNoPartialNewSet()
    {
        using var temp = new TempDirectory();
        var source = CreateSource(temp);
        var config = CreateConfig(temp, source);
        var history = CreateHistory(config);
        var first = await new EnvironmentBackupService(
            config, history, new SuccessfulInventory()).BackupAsync();
        var failing = new EnvironmentBackupService(
            config, history, new SuccessfulInventory(), archiveWriter: new FailingArchiveWriter());

        var result = await failing.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during archive creation.", result.ErrorMessage);
        Assert.Equal(first.FilePath, Assert.Single(PublishedArchives(config)));
        Assert.True(File.Exists(first.FilePath + ".sha256"));
        Assert.True(File.Exists(first.FilePath + ".json"));
        Assert.DoesNotContain("sentinel-value", result.ErrorMessage);
    }

    [Fact]
    public async Task Backup_LockContentionIsRecordedWithoutPublishing()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp));
        var history = CreateHistory(config);
        FileStream LockFactory(string backupDirectory, string targetName, string operation)
            => throw new InvalidOperationException("sentinel-value");
        var service = new EnvironmentBackupService(
            config, history, new SuccessfulInventory(), lockFactory: LockFactory);

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during lock acquisition.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
        var record = Assert.Single(await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName));
        Assert.Equal("lock_unavailable", record.ErrorMessage);
    }

    [Fact]
    public async Task Backup_HistoryCompletionFailureRetainsCompletePublishedSet()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp));
        var innerHistory = CreateHistory(config);
        var history = new CompletionFailingHistory(innerHistory);
        var service = new EnvironmentBackupService(config, history, new SuccessfulInventory());

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during history finalization.", result.ErrorMessage);
        var artifact = Assert.Single(PublishedArchives(config));
        Assert.True(File.Exists(artifact + ".sha256"));
        Assert.True(File.Exists(artifact + ".json"));
        Assert.Equal(HistoryOperationStatus.Failed,
            Assert.Single(await innerHistory.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName)).Status);
    }

    [Fact]
    public async Task Backup_RejectsSourceOverlappingBackupDirectory()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, temp.Path);
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(config, history, new SuccessfulInventory());

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during source capture.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
    }

    [Fact]
    public async Task Backup_CancellationRecordsCancelledAndPublishesNothing()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp));
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(config, history, new CancellingInventory());

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup was cancelled.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
        var record = Assert.Single(await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName));
        Assert.Equal(HistoryOperationStatus.Cancelled, record.Status);
        Assert.Equal("operation_cancelled", record.ErrorMessage);
    }

    [Fact]
    public async Task Backup_RetentionDeletesOnlyCompleteOldTriple()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp), new RetentionPolicy
        {
            MaxCount = 1,
            MaxAgeDays = 30,
            Mode = RetentionMode.Either,
            KeepAtLeast = 0
        });
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero));
        var history = CreateHistory(config, clock);
        var service = new EnvironmentBackupService(
            config, history, new SuccessfulInventory(), timeProvider: clock);

        var first = await service.BackupAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await service.BackupAsync();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(File.Exists(first.FilePath));
        Assert.False(File.Exists(first.FilePath + ".sha256"));
        Assert.False(File.Exists(first.FilePath + ".json"));
        var remaining = Assert.Single(PublishedArchives(config));
        Assert.Equal(second.FilePath, remaining);
        Assert.True(File.Exists(remaining + ".sha256"));
        Assert.True(File.Exists(remaining + ".json"));
    }

    [Fact]
    public async Task Backup_CleansStaleOwnedStagingAndUsesPrivateModesOnLinux()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp));
        var environmentDirectory = Path.Combine(config.BackupDirectory, "environment");
        var stale = Path.Combine(environmentDirectory, ".staging-abandoned");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var orphanChecksum = Path.Combine(
            environmentDirectory, "environment_2026-08-01_010101_orphan.tar.gz.sha256");
        var orphanDescriptor = Path.Combine(
            environmentDirectory, "environment_2026-08-01_010101_orphan.tar.gz.json");
        File.WriteAllText(orphanChecksum, "orphan");
        File.WriteAllText(orphanDescriptor, "orphan");
        File.SetLastWriteTimeUtc(orphanChecksum, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(orphanDescriptor, DateTime.UtcNow.AddDays(-2));
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(config, history, new SuccessfulInventory());

        var result = await service.BackupAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(Directory.Exists(stale));
        Assert.False(File.Exists(orphanChecksum));
        Assert.False(File.Exists(orphanDescriptor));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(environmentDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(result.FilePath!));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(result.FilePath + ".sha256"));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(result.FilePath + ".json"));
        }
    }

    [Fact]
    public async Task Backup_InvalidArchiveFailsVerificationWithoutPublishing()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp));
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(
            config, history, new SuccessfulInventory(), archiveWriter: new InvalidArchiveWriter());

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during archive creation.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
    }

    [Fact]
    public async Task Backup_PromotionFailureRemovesAlreadyPromotedSidecars()
    {
        using var temp = new TempDirectory();
        var config = CreateConfig(temp, CreateSource(temp));
        var history = CreateHistory(config);
        var moveCount = 0;
        void Promote(string source, string destination)
        {
            moveCount++;
            if (moveCount == 2)
                throw new IOException("sentinel-value");
            File.Move(source, destination, overwrite: false);
        }
        var service = new EnvironmentBackupService(
            config, history, new SuccessfulInventory(), promoteFile: Promote);

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during artifact publication.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
        var environmentDirectory = Path.Combine(config.BackupDirectory, "environment");
        Assert.Empty(Directory.GetFiles(environmentDirectory, "environment_*"));
        Assert.Equal("publication_failed", Assert.Single(
            await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName)).ErrorMessage);
    }

    [Fact]
    public async Task Backup_VolumeFailurePublishesNothingAndUsesSanitizedError()
    {
        using var temp = new TempDirectory();
        var config = CreateVolumeConfig(temp);
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(
            config, history, new SuccessfulInventory(), new FailingVolumeArchiver());

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during source capture.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
        Assert.Equal("source_capture_failed", Assert.Single(
            await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName)).ErrorMessage);
    }

    [Fact]
    public async Task Backup_RejectsSymlinkThatCouldBypassSecretExclusions()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var temp = new TempDirectory();
        var source = CreateSource(temp);
        var secret = Path.Combine(temp.Path, ".env");
        File.WriteAllText(secret, "SECRET=sentinel-value");
        File.CreateSymbolicLink(Path.Combine(source, "innocent-name"), secret);
        var config = CreateConfig(temp, source);
        var history = CreateHistory(config);
        var service = new EnvironmentBackupService(config, history, new SuccessfulInventory());

        var result = await service.BackupAsync();

        Assert.False(result.Success);
        Assert.Equal("Environment backup failed during source capture.", result.ErrorMessage);
        Assert.Empty(PublishedArchives(config));
        Assert.DoesNotContain("sentinel-value", Assert.Single(
            await history.GetRecordsByTargetAsync(EnvironmentBackupService.HistoryTargetName)).ErrorMessage);
    }

    private static BackupConfig CreateConfig(
        TempDirectory temp,
        string source,
        RetentionPolicy? environmentRetention = null)
    {
        var backupDirectory = Path.Combine(temp.Path, "backups");
        return new BackupConfig
        {
            BackupDirectory = backupDirectory,
            StateDirectory = Path.Combine(temp.Path, "state"),
            ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
            Retention = new RetentionPolicy { MaxCount = 2, MaxAgeDays = 30 },
            Files =
            [
                new FileTarget
                {
                    Name = "app-files",
                    Paths = [source],
                    Schedule = "daily"
                }
            ],
            Environment = new EnvironmentBackupConfig
            {
                Enabled = true,
                AcknowledgePlaintext = true,
                Targets = ["app-files"],
                Retention = environmentRetention
            }
        };
    }

    private static string CreateSource(TempDirectory temp)
    {
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "app.conf"), "enabled=true");
        return source;
    }

    private static BackupConfig CreateVolumeConfig(TempDirectory temp) => new()
    {
        BackupDirectory = Path.Combine(temp.Path, "backups"),
        StateDirectory = Path.Combine(temp.Path, "state"),
        ConfigPath = Path.Combine(temp.Path, "lutra.yaml"),
        Retention = new RetentionPolicy(),
        Volumes =
        [
            new VolumeTarget { Name = "app-data", Volume = "app_data", Schedule = "daily" }
        ],
        Environment = new EnvironmentBackupConfig
        {
            Enabled = true,
            AcknowledgePlaintext = true,
            Targets = ["app-data"]
        }
    };

    private static SqliteBackupHistoryRepository CreateHistory(
        BackupConfig config,
        TimeProvider? timeProvider = null)
        => new(
            new LutraDatabase(config.StateDirectory!, config.ConfigPath!, config.BackupDirectory),
            timeProvider);

    private static string[] PublishedArchives(BackupConfig config)
    {
        var directory = Path.Combine(config.BackupDirectory, "environment");
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "environment_*.tar.gz")
            : [];
    }

    private static async Task<List<string>> ReadNestedPayloadEntriesAsync(string archive)
    {
        await using var file = File.OpenRead(archive);
        await using var outerGzip = new GZipStream(file, CompressionMode.Decompress);
        using var outer = new TarReader(outerGzip);
        await using var payload = new MemoryStream();
        while (await outer.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name.StartsWith("payload/files/", StringComparison.Ordinal))
            {
                await entry.DataStream!.CopyToAsync(payload);
                break;
            }
        }
        payload.Position = 0;
        await using var innerGzip = new GZipStream(payload, CompressionMode.Decompress);
        using var inner = new TarReader(innerGzip);
        var names = new List<string>();
        while (await inner.GetNextEntryAsync() is { } entry)
            names.Add(entry.Name);
        return names;
    }

    private sealed class SuccessfulInventory : IInventoryCollector
    {
        public Task<InventorySnapshot> CollectSnapshotAsync(
            InventoryCollectionPolicy? policy = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new InventorySnapshot
            {
                CapturedAt = DateTime.UtcNow,
                Host = "test-host",
                LutraVersion = "test",
                Sections =
                [
                    new InventorySection { Name = "os", Required = true, Status = InventoryCollectorStatus.Succeeded },
                    new InventorySection { Name = "packages", Required = true, Status = InventoryCollectorStatus.Succeeded },
                    new InventorySection { Name = "docker", Required = false, Status = InventoryCollectorStatus.NotApplicable },
                    new InventorySection { Name = "systemd", Required = false, Status = InventoryCollectorStatus.NotApplicable }
                ]
            });
    }

    private sealed class FailedInventory(string sentinel) : IInventoryCollector
    {
        public Task<InventorySnapshot> CollectSnapshotAsync(
            InventoryCollectionPolicy? policy = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new InventorySnapshot
            {
                CapturedAt = DateTime.UtcNow,
                Host = "test-host",
                LutraVersion = sentinel,
                Sections =
                [
                    new InventorySection
                    {
                        Name = "packages",
                        Required = true,
                        Status = InventoryCollectorStatus.Failed,
                        ErrorCategory = "command_failed"
                    }
                ]
            });
    }

    private sealed class FailingArchiveWriter : IEnvironmentArchiveWriter
    {
        public Task WriteAsync(EnvironmentArchiveWriteRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("sentinel-value");
    }

    private sealed class InvalidArchiveWriter : IEnvironmentArchiveWriter
    {
        public Task WriteAsync(EnvironmentArchiveWriteRequest request, CancellationToken cancellationToken)
            => File.WriteAllTextAsync(request.OutputPath, "not-an-archive", cancellationToken);
    }

    private sealed class FailingVolumeArchiver : IEnvironmentVolumeArchiver
    {
        public Task CreateAsync(string volume, string outputPath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("sentinel-value");
    }

    private sealed class CancellingInventory : IInventoryCollector
    {
        public Task<InventorySnapshot> CollectSnapshotAsync(
            InventoryCollectionPolicy? policy = null,
            CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);
    }

    private sealed class ManualClock(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class CompletionFailingHistory(IBackupHistoryService inner) : IBackupHistoryService
    {
        public Task<HistoryOperationLease> BeginOperationAsync(string targetName, HistoryOperationType operationType, CancellationToken cancellationToken = default)
            => inner.BeginOperationAsync(targetName, operationType, cancellationToken);
        public Task CompleteOperationAsync(Guid operationId, Guid leaseId, HistoryOperationCompletion completion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("sentinel-value");
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

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Lutra.Core.Configuration;
using Lutra.Core.Encryption;
using Lutra.Core.Files;
using Lutra.Core.History;
using Lutra.Core.Volumes;

namespace Lutra.Core.Backup;

public class BackupOrchestrator
{
    private readonly IReadOnlyDictionary<DatabaseType, IBackupProvider> _providers;
    private readonly IProcessExecutor _processExecutor;
    private readonly IBackupHistoryService _historyService;
    private readonly BackupConfig _config;

    public BackupOrchestrator(
        IEnumerable<IBackupProvider> providers,
        IProcessExecutor processExecutor,
        IBackupHistoryService historyService,
        BackupConfig config)
    {
        _providers = providers.ToDictionary(p => p.Type);
        _processExecutor = processExecutor;
        _historyService = historyService;
        _config = config;
    }

    public async Task<BackupResult> BackupAsync(DatabaseTarget target, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(target.Type, out var provider))
        {
            return new BackupResult
            {
                TargetName = target.Name,
                Success = false,
                Timestamp = DateTime.UtcNow,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"No backup provider registered for database type '{target.Type}'."
            };
        }

        return await RunBackupAsync(
            target,
            provider.GetFileExtension(target),
            target.Compression,
            async (tempFilePath, backupId, ct) =>
            {
                var command = provider.BuildDumpCommand(target, backupId);
                if (provider.StreamsToStdout)
                {
                    await ExecuteStreamingBackup(command, tempFilePath, target.Compression, ct);
                }
                else
                {
                    await ExecuteFileBasedBackup(command, provider, target, backupId, tempFilePath, ct);
                }
            },
            (fileName, fileSize, sha256, startedAt, duration) => new BackupManifest
            {
                TargetName = target.Name,
                TargetType = "database",
                DatabaseType = target.Type,
                Database = target.Database,
                Container = target.Container,
                BackupFileName = fileName,
                FileSizeBytes = fileSize,
                Sha256 = sha256,
                Compression = target.Compression,
                Format = target.Format,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                DurationMs = (long)duration.TotalMilliseconds,
                LutraVersion = GetVersion(),
                Success = true,
                Encrypted = GetEncryption(target) is not null,
                EncryptionRecipientFingerprint = GetRecipientFingerprint(target)
            },
            cancellationToken);
    }

    public async Task<BackupResult> BackupFilesAsync(FileTarget target, CancellationToken cancellationToken = default)
    {
        return await RunBackupAsync(
            target,
            ".tar",
            target.Compression,
            (tempFilePath, _, ct) => FileArchive.CreateAsync(
                target.Paths, target.Exclude ?? [], tempFilePath, target.Compression, ct),
            (fileName, fileSize, sha256, startedAt, duration) => new BackupManifest
            {
                TargetName = target.Name,
                TargetType = "files",
                Paths = target.Paths,
                BackupFileName = fileName,
                FileSizeBytes = fileSize,
                Sha256 = sha256,
                Compression = target.Compression,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                DurationMs = (long)duration.TotalMilliseconds,
                LutraVersion = GetVersion(),
                Success = true,
                Encrypted = GetEncryption(target) is not null,
                EncryptionRecipientFingerprint = GetRecipientFingerprint(target)
            },
            cancellationToken);
    }

    public async Task<BackupResult> BackupVolumeAsync(VolumeTarget target, CancellationToken cancellationToken = default)
    {
        return await RunBackupAsync(
            target,
            ".tar",
            target.Compression,
            (tempFilePath, _, ct) => DockerVolumeArchive.CreateAsync(
                target.Volume, tempFilePath, target.Compression, ct),
            (fileName, fileSize, sha256, startedAt, duration) => new BackupManifest
            {
                TargetName = target.Name,
                TargetType = "volume",
                Volume = target.Volume,
                BackupFileName = fileName,
                FileSizeBytes = fileSize,
                Sha256 = sha256,
                Compression = target.Compression,
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow,
                DurationMs = (long)duration.TotalMilliseconds,
                LutraVersion = GetVersion(),
                Success = true,
                Encrypted = GetEncryption(target) is not null,
                EncryptionRecipientFingerprint = GetRecipientFingerprint(target)
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<BackupResult>> BackupAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<BackupResult>();

        foreach (var target in _config.Databases)
        {
            results.Add(await BackupAsync(target, cancellationToken));
        }

        foreach (var target in _config.Files)
        {
            results.Add(await BackupFilesAsync(target, cancellationToken));
        }

        foreach (var target in _config.Volumes)
        {
            results.Add(await BackupVolumeAsync(target, cancellationToken));
        }

        return results;
    }

    public async Task<int> CleanupAsync(IBackupTarget target, CancellationToken cancellationToken = default)
    {
        return await ApplyRetentionAsync(target, dryRun: false, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupCleanupCandidate>> PreviewCleanupAsync(
        IBackupTarget target,
        CancellationToken cancellationToken = default)
    {
        return await GetRetentionCandidatesAsync(target, cancellationToken);
    }

    private async Task<BackupResult> RunBackupAsync(
        IBackupTarget target,
        string extension,
        CompressionType compression,
        Func<string, string, CancellationToken, Task> writeBackupAsync,
        Func<string, long, string, DateTime, TimeSpan, BackupManifest> createManifest,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var backupId = Guid.NewGuid().ToString("N")[..12];
        string? tempFilePath = null;
        string? finalFilePath = null;
        string? fileName = null;
        var finalMoved = false;
        var artifactRecorded = false;

        try
        {
            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Backup");

            fileName = BackupFileNaming.Build(target.Name, startTime, backupId, extension, compression);
            var encryption = GetEncryption(target);
            if (encryption is not null)
                fileName += ".age";
            var targetDir = Path.Combine(_config.BackupDirectory, target.Name);
            Directory.CreateDirectory(targetDir);
            finalFilePath = Path.Combine(targetDir, fileName);
            tempFilePath = Path.Combine(targetDir, $".{fileName}.tmp");

            await writeBackupAsync(tempFilePath, backupId, cancellationToken);

            if (encryption is not null)
            {
                var encryptedTempPath = tempFilePath + ".encrypted";
                try
                {
                    await AgeEncryption.EncryptAsync(
                        tempFilePath, encryptedTempPath, encryption.Recipient, cancellationToken);
                }
                catch
                {
                    DeleteIfExists(encryptedTempPath);
                    throw;
                }
                DeleteIfExists(tempFilePath);
                tempFilePath = encryptedTempPath;
            }

            File.Move(tempFilePath, finalFilePath);
            finalMoved = true;
            tempFilePath = null;

            var fileInfo = new FileInfo(finalFilePath);
            var sha256 = await BackupIntegrity.ComputeSha256Async(finalFilePath, cancellationToken);
            stopwatch.Stop();

            var manifest = createManifest(fileName, fileInfo.Length, sha256, startTime, stopwatch.Elapsed);

            await BackupIntegrity.WriteChecksumFileAsync(finalFilePath, sha256, cancellationToken);
            await BackupIntegrity.WriteManifestAsync(finalFilePath, manifest, cancellationToken);

            var record = new BackupRecord
            {
                TargetName = target.Name,
                Timestamp = startTime,
                FileName = fileName,
                FileSizeBytes = fileInfo.Length,
                Sha256 = sha256,
                ManifestFileName = Path.GetFileName(BackupIntegrity.GetManifestPath(finalFilePath)),
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                Success = true
            };
            await _historyService.AddRecordAsync(record, cancellationToken);
            artifactRecorded = true;

            try
            {
                await ApplyRetentionAsync(target, dryRun: false, cancellationToken);
            }
            catch
            {
                // A retention failure should not turn a completed, recorded backup into a failed backup.
            }

            return new BackupResult
            {
                TargetName = target.Name,
                Success = true,
                Timestamp = startTime,
                Duration = stopwatch.Elapsed,
                FilePath = finalFilePath,
                FileSizeBytes = fileInfo.Length,
                Sha256 = sha256
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DeleteIfExists(tempFilePath);

            if (finalMoved && !artifactRecorded && finalFilePath is not null && fileName is not null)
            {
                DeleteIfExists(finalFilePath);
                DeleteIfExists(BackupIntegrity.GetChecksumPath(finalFilePath));
                DeleteIfExists(BackupIntegrity.GetManifestPath(finalFilePath));
            }

            var failureRecord = new BackupRecord
            {
                TargetName = target.Name,
                Timestamp = startTime,
                FileName = string.Empty,
                FileSizeBytes = 0,
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                Success = false,
                ErrorMessage = ex.Message
            };
            await _historyService.AddRecordAsync(failureRecord, cancellationToken);

            return new BackupResult
            {
                TargetName = target.Name,
                Success = false,
                Timestamp = startTime,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task ExecuteStreamingBackup(
        DockerExecCommand command, string filePath, CompressionType compression, CancellationToken cancellationToken)
    {
        using var result = await _processExecutor.ExecuteAsync(command, cancellationToken);

        if (!result.IsSuccess)
            throw new InvalidOperationException($"Backup command failed (exit code {result.ExitCode}): {result.StandardError}");

        await WriteStreamToFile(result.OutputStream, filePath, compression, cancellationToken);
    }

    private async Task ExecuteFileBasedBackup(
        DockerExecCommand command, IBackupProvider provider, DatabaseTarget target,
        string backupId, string filePath, CancellationToken cancellationToken)
    {
        // Step 1: Run the dump command (writes to a file inside the container)
        using var dumpResult = await _processExecutor.ExecuteAsync(command, cancellationToken);

        if (!dumpResult.IsSuccess)
            throw new InvalidOperationException($"Backup command failed (exit code {dumpResult.ExitCode}): {dumpResult.StandardError}");

        var containerPath = provider.GetContainerBackupPath(target, backupId)
            ?? throw new InvalidOperationException("Provider does not stream to stdout but returned no container backup path.");

        try
        {
            // Step 2: Stream the file out of the container via cat
            var catCommand = new DockerExecCommand(
                ContainerName: command.ContainerName,
                Command: "cat",
                Arguments: [containerPath]
            );

            using var catResult = await _processExecutor.ExecuteAsync(catCommand, cancellationToken);

            if (!catResult.IsSuccess)
                throw new InvalidOperationException($"Failed to extract backup file from container: {catResult.StandardError}");

            await WriteStreamToFile(catResult.OutputStream, filePath, target.Compression, cancellationToken);
        }
        finally
        {
            // Step 3: Clean up the temp file inside the container
            var rmCommand = new DockerExecCommand(
                ContainerName: command.ContainerName,
                Command: "rm",
                Arguments: ["-f", containerPath]
            );

            using var rmResult = await _processExecutor.ExecuteAsync(rmCommand, cancellationToken);
        }
    }

    private static async Task WriteStreamToFile(
        Stream input, string outputPath, CompressionType compression, CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        if (compression == CompressionType.Gzip)
        {
            await using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
            await input.CopyToAsync(gzipStream, cancellationToken);
        }
        else
        {
            await input.CopyToAsync(fileStream, cancellationToken);
        }
    }

    private async Task<int> ApplyRetentionAsync(IBackupTarget target, bool dryRun, CancellationToken cancellationToken)
    {
        var candidates = await GetRetentionCandidatesAsync(target, cancellationToken);
        var deletedCount = 0;

        foreach (var candidate in candidates)
        {
            if (!dryRun)
            {
                foreach (var filePath in candidate.PathsToDelete)
                    DeleteIfExists(filePath);

                await _historyService.RemoveRecordAsync(target.Name, candidate.Record.FileName, cancellationToken);
            }
            deletedCount++;
        }

        return deletedCount;
    }

    private async Task<IReadOnlyList<BackupCleanupCandidate>> GetRetentionCandidatesAsync(
        IBackupTarget target,
        CancellationToken cancellationToken)
    {
        var retention = target.Retention ?? _config.Retention;
        var records = await _historyService.GetRecordsByTargetAsync(target.Name, cancellationToken);

        var successRecords = records
            .Where(r => r.Success && r.RecordType is null)
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        var cutoffDate = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);

        return successRecords
            .Select((record, index) => new
            {
                Record = record,
                Index = index,
                CountExceeded = index >= retention.MaxCount,
                AgeExceeded = record.Timestamp < cutoffDate
            })
            .Where(item => item.Index >= retention.KeepAtLeast)
            .Where(item => retention.Mode == RetentionMode.Both
                ? item.CountExceeded && item.AgeExceeded
                : item.CountExceeded || item.AgeExceeded)
            .Select(item =>
            {
                var record = item.Record;
                var backupPath = Path.Combine(_config.BackupDirectory, target.Name, record.FileName);
                string[] paths =
                [
                    backupPath,
                    BackupIntegrity.GetChecksumPath(backupPath),
                    BackupIntegrity.GetManifestPath(backupPath)
                ];

                return new BackupCleanupCandidate(record, paths);
            })
            .ToList();
    }

    private EncryptionConfig? GetEncryption(IBackupTarget target)
        => target.Encryption ?? _config.Encryption;

    private string? GetRecipientFingerprint(IBackupTarget target)
        => GetEncryption(target) is { } encryption
            ? AgeEncryption.RecipientFingerprint(encryption.Recipient)
            : null;

    private static void DeleteIfExists(string? path)
    {
        if (path is not null && File.Exists(path))
            File.Delete(path);
    }

    private static string GetVersion()
    {
        return typeof(BackupOrchestrator).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(BackupOrchestrator).Assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}

public sealed record BackupCleanupCandidate(BackupRecord Record, IReadOnlyList<string> PathsToDelete);

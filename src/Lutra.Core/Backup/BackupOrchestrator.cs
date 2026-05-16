using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Lutra.Core.Configuration;
using Lutra.Core.History;

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
            await using var targetLock = AcquireTargetLock(target);

            if (!_providers.TryGetValue(target.Type, out var provider))
                throw new NotSupportedException($"No backup provider registered for database type '{target.Type}'.");

            var command = provider.BuildDumpCommand(target, backupId);
            var extension = provider.GetFileExtension(target);
            fileName = BuildFileName(target.Name, startTime, backupId, extension, target.Compression);
            var targetDir = Path.Combine(_config.BackupDirectory, target.Name);
            Directory.CreateDirectory(targetDir);
            finalFilePath = Path.Combine(targetDir, fileName);
            tempFilePath = Path.Combine(targetDir, $".{fileName}.tmp");

            if (provider.StreamsToStdout)
            {
                await ExecuteStreamingBackup(command, tempFilePath, target.Compression, cancellationToken);
            }
            else
            {
                await ExecuteFileBasedBackup(command, provider, target, backupId, tempFilePath, cancellationToken);
            }

            File.Move(tempFilePath, finalFilePath);
            finalMoved = true;
            tempFilePath = null;

            var fileInfo = new FileInfo(finalFilePath);
            var sha256 = await BackupIntegrity.ComputeSha256Async(finalFilePath, cancellationToken);
            stopwatch.Stop();

            var manifest = new BackupManifest
            {
                TargetName = target.Name,
                DatabaseType = target.Type,
                Database = target.Database,
                Container = target.Container,
                BackupFileName = fileName,
                FileSizeBytes = fileInfo.Length,
                Sha256 = sha256,
                Compression = target.Compression,
                Format = target.Format,
                StartedAt = startTime,
                CompletedAt = DateTime.UtcNow,
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                LutraVersion = GetVersion(),
                Success = true
            };

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

    public async Task<IReadOnlyList<BackupResult>> BackupAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<BackupResult>();

        foreach (var target in _config.Databases)
        {
            results.Add(await BackupAsync(target, cancellationToken));
        }

        return results;
    }

    public async Task<int> CleanupAsync(DatabaseTarget target, CancellationToken cancellationToken = default)
    {
        return await ApplyRetentionAsync(target, dryRun: false, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupCleanupCandidate>> PreviewCleanupAsync(
        DatabaseTarget target,
        CancellationToken cancellationToken = default)
    {
        return await GetRetentionCandidatesAsync(target, cancellationToken);
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

    private async Task<int> ApplyRetentionAsync(DatabaseTarget target, bool dryRun, CancellationToken cancellationToken)
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
        DatabaseTarget target,
        CancellationToken cancellationToken)
    {
        var retention = target.Retention ?? _config.Retention;
        var records = await _historyService.GetRecordsByTargetAsync(target.Name, cancellationToken);

        var successRecords = records
            .Where(r => r.Success)
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        if (successRecords.Count <= retention.MaxCount)
            return [];

        var cutoffDate = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);

        return successRecords
            .Skip(retention.MaxCount)
            .Where(r => r.Timestamp < cutoffDate)
            .Select(record =>
            {
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

    private FileStream AcquireTargetLock(DatabaseTarget target)
    {
        var lockDir = Path.Combine(_config.BackupDirectory, ".locks");
        Directory.CreateDirectory(lockDir);

        var lockPath = Path.Combine(lockDir, SanitizeFileComponent(target.Name) + ".lock");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            LockFile(stream);
            return stream;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Backup for target '{target.Name}' is already running.", ex);
        }
    }

    private static string BuildFileName(
        string targetName,
        DateTime timestamp,
        string backupId,
        string extension,
        CompressionType compression)
    {
        var name = $"{targetName}_{timestamp:yyyy-MM-dd}_{timestamp:HHmmss}_{backupId}{extension}";

        if (compression == CompressionType.Gzip)
            name += ".gz";

        return name;
    }

    private static string SanitizeFileComponent(string value)
    {
        return new string(value.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
    }

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

    private static void LockFile(FileStream stream)
    {
        if (!OperatingSystem.IsMacOS())
            stream.Lock(0, 0);
    }
}

public sealed record BackupCleanupCandidate(BackupRecord Record, IReadOnlyList<string> PathsToDelete);

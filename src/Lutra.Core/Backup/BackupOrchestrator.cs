using System.Diagnostics;
using System.IO.Compression;
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

        try
        {
            if (!_providers.TryGetValue(target.Type, out var provider))
                throw new NotSupportedException($"No backup provider registered for database type '{target.Type}'.");

            var command = provider.BuildDumpCommand(target);
            var extension = provider.GetFileExtension(target);
            var fileName = BuildFileName(target.Name, startTime, extension, target.Compression);
            var targetDir = Path.Combine(_config.BackupDirectory, target.Name);
            Directory.CreateDirectory(targetDir);
            var filePath = Path.Combine(targetDir, fileName);

            if (provider.StreamsToStdout)
            {
                await ExecuteStreamingBackup(command, filePath, target.Compression, cancellationToken);
            }
            else
            {
                await ExecuteFileBasedBackup(command, provider, target, filePath, cancellationToken);
            }

            var fileInfo = new FileInfo(filePath);
            stopwatch.Stop();

            var record = new BackupRecord
            {
                TargetName = target.Name,
                Timestamp = startTime,
                FileName = fileName,
                FileSizeBytes = fileInfo.Length,
                DurationMs = (long)stopwatch.Elapsed.TotalMilliseconds,
                Success = true
            };
            await _historyService.AddRecordAsync(record, cancellationToken);

            await ApplyRetentionAsync(target, cancellationToken);

            return new BackupResult
            {
                TargetName = target.Name,
                Success = true,
                Timestamp = startTime,
                Duration = stopwatch.Elapsed,
                FilePath = filePath,
                FileSizeBytes = fileInfo.Length
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

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
        return await ApplyRetentionAsync(target, cancellationToken);
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
        string filePath, CancellationToken cancellationToken)
    {
        // Step 1: Run the dump command (writes to a file inside the container)
        using var dumpResult = await _processExecutor.ExecuteAsync(command, cancellationToken);

        if (!dumpResult.IsSuccess)
            throw new InvalidOperationException($"Backup command failed (exit code {dumpResult.ExitCode}): {dumpResult.StandardError}");

        var containerPath = provider.GetContainerBackupPath(target)
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

    private async Task<int> ApplyRetentionAsync(DatabaseTarget target, CancellationToken cancellationToken)
    {
        var retention = target.Retention ?? _config.Retention;
        var records = await _historyService.GetRecordsByTargetAsync(target.Name, cancellationToken);

        var successRecords = records
            .Where(r => r.Success)
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        if (successRecords.Count <= retention.MaxCount)
            return 0;

        var cutoffDate = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);
        var deletedCount = 0;

        // Delete only when BOTH conditions are met: exceeds max_count AND older than max_age_days
        var candidates = successRecords
            .Skip(retention.MaxCount)
            .Where(r => r.Timestamp < cutoffDate);

        foreach (var record in candidates)
        {
            var filePath = Path.Combine(_config.BackupDirectory, target.Name, record.FileName);

            if (File.Exists(filePath))
                File.Delete(filePath);

            await _historyService.RemoveRecordAsync(target.Name, record.FileName, cancellationToken);
            deletedCount++;
        }

        return deletedCount;
    }

    private static string BuildFileName(string targetName, DateTime timestamp, string extension, CompressionType compression)
    {
        var name = $"{targetName}_{timestamp:yyyy-MM-dd}_{timestamp:HHmmss}{extension}";

        if (compression == CompressionType.Gzip)
            name += ".gz";

        return name;
    }
}

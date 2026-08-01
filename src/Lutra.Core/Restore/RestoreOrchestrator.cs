using System.Diagnostics;
using System.IO.Compression;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Files;
using Lutra.Core.History;
using Lutra.Core.Volumes;

namespace Lutra.Core.Restore;

/// <summary>
/// Coordinates destructive restores and non-destructive test-restores (verification)
/// of database backups produced by <see cref="BackupOrchestrator"/>.
/// </summary>
public class RestoreOrchestrator
{
    private readonly IReadOnlyDictionary<DatabaseType, IRestoreProvider> _providers;
    private readonly IProcessExecutor _processExecutor;
    private readonly IBackupHistoryService _historyService;
    private readonly BackupConfig _config;

    public RestoreOrchestrator(
        IEnumerable<IRestoreProvider> providers,
        IProcessExecutor processExecutor,
        IBackupHistoryService historyService,
        BackupConfig config)
    {
        _providers = providers.ToDictionary(p => p.Type);
        _processExecutor = processExecutor;
        _historyService = historyService;
        _config = config;
    }

    /// <summary>
    /// Restores a backup file into the target's configured database, replacing its
    /// current contents. This is a destructive operation; callers are responsible
    /// for obtaining user confirmation beforehand.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        DatabaseTarget target,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(backupFilePath))
                throw new FileNotFoundException($"Backup file not found: {backupFilePath}");
            if (target.Type == DatabaseType.SqlServer
                && GetSqlServerBackupKind(backupFilePath) != SqlServerBackupKind.Full)
                throw new InvalidOperationException("Differential and log backups must be restored with an ordered --chain beginning with a full backup.");

            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Restore");

            var provider = GetProvider(target.Type);
            var source = DescribeSource(backupFilePath);

            await RestoreIntoAsync(provider, target, target.Database, source, cancellationToken);

            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = true,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = target.Database
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = false,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = target.Database,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<RestoreResult> RestoreSqlServerChainAsync(
        DatabaseTarget target,
        IReadOnlyList<string> backupFiles,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (target.Type != DatabaseType.SqlServer)
                throw new InvalidOperationException("Restore chains are supported only for SQL Server targets.");
            if (backupFiles.Count == 0)
                throw new InvalidOperationException("At least one chain file is required.");

            var kinds = backupFiles.Select(GetSqlServerBackupKind).ToList();
            if (kinds[0] != SqlServerBackupKind.Full)
                throw new InvalidOperationException("A SQL Server restore chain must begin with a full backup.");
            if (kinds.Skip(1).Any(kind => kind == SqlServerBackupKind.Full)
                || kinds.Count(kind => kind == SqlServerBackupKind.Differential) > 1
                || kinds.SkipWhile(kind => kind != SqlServerBackupKind.Log).Any(kind => kind != SqlServerBackupKind.Log))
                throw new InvalidOperationException("Chain order must be full, optional differential, then transaction logs.");

            foreach (var path in backupFiles)
            {
                EnsureNotEncrypted(path);
                var integrity = await BackupIntegrity.VerifyFileAsync(path, cancellationToken);
                if (!integrity.Success)
                    throw new InvalidOperationException($"Integrity check failed for '{Path.GetFileName(path)}': {integrity.Message}");
            }

            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Restore chain");
            var provider = (SqlServerRestoreProvider)GetProvider(DatabaseType.SqlServer);
            for (var index = 0; index < backupFiles.Count; index++)
            {
                var source = DescribeSource(backupFiles[index]);
                var containerPath = provider.GetContainerRestoreFilePath(target, Guid.NewGuid().ToString("N")[..12]);
                try
                {
                    await using var input = OpenSourceStream(source);
                    var uploadCommand = new DockerExecCommand(target.Container, "sh", ["-c", $"cat > '{containerPath}'"]);
                    using (var upload = await _processExecutor.ExecuteWithInputAsync(uploadCommand, input, cancellationToken))
                    {
                        if (!upload.IsSuccess)
                            throw new InvalidOperationException($"Failed to upload chain file: {upload.StandardError}");
                    }
                    await ExecuteCheckedAsync(
                        provider.BuildChainRestoreCommand(
                            target, containerPath, kinds[index], index == 0, index == backupFiles.Count - 1),
                        $"Failed to restore chain file {Path.GetFileName(backupFiles[index])}", cancellationToken);
                }
                finally
                {
                    using var removed = await _processExecutor.ExecuteAsync(
                        new DockerExecCommand(target.Container, "rm", ["-f", containerPath]), CancellationToken.None);
                }
            }

            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = true,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = target.Database
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = false,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = target.Database,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Verifies a backup file by restoring it into a temporary database, running a
    /// minimal validation query, and dropping the temporary database afterwards.
    /// Never modifies the target's configured database. The result is recorded in
    /// backup history as a <c>"verify"</c> record.
    /// </summary>
    public async Task<VerifyResult> TestRestoreAsync(
        DatabaseTarget target,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var restoreId = Guid.NewGuid().ToString("N")[..12];
        var checksumValid = false;
        IRestoreProvider? provider = null;
        string? testDatabase = null;

        try
        {
            if (!File.Exists(backupFilePath))
                throw new FileNotFoundException($"Backup file not found: {backupFilePath}");

            var integrity = await BackupIntegrity.VerifyFileAsync(backupFilePath, cancellationToken);
            if (!integrity.Success)
                throw new InvalidOperationException($"Integrity check failed: {integrity.Message}");
            checksumValid = true;

            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Verify");

            provider = GetProvider(target.Type);
            var source = DescribeSource(backupFilePath);
            testDatabase = provider.GenerateTestDatabaseName(target, restoreId);

            var createCommand = provider.BuildCreateDatabaseCommand(target, testDatabase);
            if (createCommand is not null)
                await ExecuteCheckedAsync(createCommand, "Failed to create temporary database", cancellationToken);

            string? validationOutput;
            try
            {
                await RestoreIntoAsync(provider, target, testDatabase, source, cancellationToken);

                using var validation = await _processExecutor.ExecuteAsync(
                    provider.BuildValidationCommand(target, testDatabase), cancellationToken);
                if (!validation.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Validation query failed (exit code {validation.ExitCode}): {validation.StandardError}");
                }
                validationOutput = await ReadOutputAsync(validation, cancellationToken);
            }
            finally
            {
                await TryDropDatabaseAsync(provider, target, testDatabase);
                testDatabase = null;
            }

            stopwatch.Stop();
            var successResult = new VerifyResult
            {
                TargetName = target.Name,
                BackupFilePath = backupFilePath,
                ChecksumValid = checksumValid,
                Success = true,
                Duration = stopwatch.Elapsed,
                ValidationDetails = BuildValidationDetails(provider.Type, validationOutput)
            };
            await RecordVerificationAsync(target, backupFilePath, successResult, cancellationToken);
            return successResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            if (provider is not null && testDatabase is not null)
                await TryDropDatabaseAsync(provider, target, testDatabase);

            var failureResult = new VerifyResult
            {
                TargetName = target.Name,
                BackupFilePath = backupFilePath,
                ChecksumValid = checksumValid,
                Success = false,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
            await RecordVerificationAsync(target, backupFilePath, failureResult, cancellationToken);
            return failureResult;
        }
    }

    /// <summary>
    /// Extracts a file-target archive into <paramref name="destinationDirectory"/>.
    /// Pass <c>/</c> to restore files to their original locations; existing files
    /// with the same paths are overwritten. Callers are responsible for obtaining
    /// user confirmation beforehand.
    /// </summary>
    public async Task<RestoreResult> RestoreFilesAsync(
        FileTarget target,
        string archiveFilePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Backup file not found: {archiveFilePath}");
            EnsureNotEncrypted(archiveFilePath);

            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Restore");

            await FileArchive.ExtractAsync(archiveFilePath, destinationDirectory, cancellationToken);

            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = true,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = destinationDirectory
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = false,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = destinationDirectory,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<RestoreResult> RestoreVolumeAsync(
        VolumeTarget target,
        string archiveFilePath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            EnsureNotEncrypted(archiveFilePath);
            var integrity = await BackupIntegrity.VerifyFileAsync(archiveFilePath, cancellationToken);
            if (!integrity.Success)
                throw new InvalidOperationException($"Integrity check failed: {integrity.Message}");
            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Restore");
            var compression = archiveFilePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? CompressionType.Gzip
                : CompressionType.None;
            await DockerVolumeArchive.RestoreAsync(target.Volume, archiveFilePath, compression, cancellationToken);
            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = true,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = target.Volume
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RestoreResult
            {
                TargetName = target.Name,
                Success = false,
                Duration = stopwatch.Elapsed,
                DestinationDatabase = target.Volume,
                ErrorMessage = ex.Message
            };
        }
    }

    public Task<VerifyResult> VerifyVolumeAsync(
        VolumeTarget target,
        string archiveFilePath,
        CancellationToken cancellationToken = default)
        => VerifyArchiveAsync(target, archiveFilePath, cancellationToken);

    /// <summary>
    /// Verifies a file-target archive by checking its checksum sidecar and reading
    /// through the archive to validate integrity. The result is recorded in backup
    /// history as a <c>"verify"</c> record.
    /// </summary>
    public Task<VerifyResult> VerifyFilesAsync(
        FileTarget target,
        string archiveFilePath,
        CancellationToken cancellationToken = default)
        => VerifyArchiveAsync(target, archiveFilePath, cancellationToken);

    private async Task<VerifyResult> VerifyArchiveAsync(
        IBackupTarget target,
        string archiveFilePath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var checksumValid = false;

        try
        {
            if (!File.Exists(archiveFilePath))
                throw new FileNotFoundException($"Backup file not found: {archiveFilePath}");
            EnsureNotEncrypted(archiveFilePath);

            var integrity = await BackupIntegrity.VerifyFileAsync(archiveFilePath, cancellationToken);
            if (!integrity.Success)
                throw new InvalidOperationException($"Integrity check failed: {integrity.Message}");
            checksumValid = true;

            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Verify");

            var entryCount = await FileArchive.CountEntriesAsync(archiveFilePath, cancellationToken);

            stopwatch.Stop();
            var successResult = new VerifyResult
            {
                TargetName = target.Name,
                BackupFilePath = archiveFilePath,
                ChecksumValid = checksumValid,
                Success = true,
                Duration = stopwatch.Elapsed,
                ValidationDetails = $"Archive is readable and contains {entryCount} entries."
            };
            await RecordVerificationAsync(target, archiveFilePath, successResult, cancellationToken);
            return successResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var failureResult = new VerifyResult
            {
                TargetName = target.Name,
                BackupFilePath = archiveFilePath,
                ChecksumValid = checksumValid,
                Success = false,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
            await RecordVerificationAsync(target, archiveFilePath, failureResult, cancellationToken);
            return failureResult;
        }
    }

    private IRestoreProvider GetProvider(DatabaseType type)
    {
        if (!_providers.TryGetValue(type, out var provider))
            throw new NotSupportedException($"No restore provider registered for database type '{type}'.");
        return provider;
    }

    private async Task RestoreIntoAsync(
        IRestoreProvider provider,
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source,
        CancellationToken cancellationToken)
    {
        await using var input = OpenSourceStream(source);

        if (provider.ReadsFromStdin)
        {
            if (destinationDatabase.Equals(target.Database, StringComparison.Ordinal))
            {
                foreach (var prepareCommand in provider.BuildDestructivePrepareCommands(target, source))
                    await ExecuteCheckedAsync(prepareCommand, "Failed to prepare database for restore", cancellationToken);
            }

            var command = provider.BuildStdinRestoreCommand(target, destinationDatabase, source);
            using var result = await _processExecutor.ExecuteWithInputAsync(command, input, cancellationToken);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Restore command failed (exit code {result.ExitCode}): {result.StandardError}");
            }
            return;
        }

        // File-based restore (SQL Server): upload the backup into the container,
        // restore from the container path, then remove the uploaded file.
        var restoreId = Guid.NewGuid().ToString("N")[..12];
        var containerPath = provider.GetContainerRestoreFilePath(target, restoreId);

        try
        {
            var uploadCommand = new DockerExecCommand(
                ContainerName: target.Container,
                Command: "sh",
                Arguments: ["-c", $"cat > '{containerPath}'"]);

            using (var upload = await _processExecutor.ExecuteWithInputAsync(uploadCommand, input, cancellationToken))
            {
                if (!upload.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Failed to upload backup file into container: {upload.StandardError}");
                }
            }

            IReadOnlyList<BackupFileEntry> backupFiles = [];
            if (!destinationDatabase.Equals(target.Database, StringComparison.Ordinal))
            {
                var listCommand = provider.BuildListBackupFilesCommand(target, containerPath)
                    ?? throw new NotSupportedException(
                        "Restoring to a different database name is not supported by this provider.");

                using var listResult = await _processExecutor.ExecuteAsync(listCommand, cancellationToken);
                if (!listResult.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Failed to read backup file list (exit code {listResult.ExitCode}): {listResult.StandardError}");
                }

                var listOutput = await ReadOutputAsync(listResult, cancellationToken);
                backupFiles = provider.ParseBackupFileList(listOutput);
            }

            var restoreCommand = provider.BuildContainerFileRestoreCommand(
                target, containerPath, destinationDatabase, backupFiles);

            using var restoreResult = await _processExecutor.ExecuteAsync(restoreCommand, cancellationToken);
            if (!restoreResult.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Restore command failed (exit code {restoreResult.ExitCode}): {restoreResult.StandardError}");
            }
        }
        finally
        {
            var rmCommand = new DockerExecCommand(
                ContainerName: target.Container,
                Command: "rm",
                Arguments: ["-f", containerPath]);

            using var rmResult = await _processExecutor.ExecuteAsync(rmCommand, CancellationToken.None);
        }
    }

    private async Task ExecuteCheckedAsync(
        DockerExecCommand command, string errorPrefix, CancellationToken cancellationToken)
    {
        using var result = await _processExecutor.ExecuteAsync(command, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"{errorPrefix} (exit code {result.ExitCode}): {result.StandardError}");
    }

    private async Task TryDropDatabaseAsync(IRestoreProvider provider, DatabaseTarget target, string database)
    {
        try
        {
            using var result = await _processExecutor.ExecuteAsync(
                provider.BuildDropDatabaseCommand(target, database), CancellationToken.None);
        }
        catch
        {
            // Best-effort cleanup of the temporary database; verification already
            // has its result at this point.
        }
    }

    private async Task RecordVerificationAsync(
        IBackupTarget target,
        string backupFilePath,
        VerifyResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var completedAt = DateTimeOffset.UtcNow;
            var record = new HistoryRecord
            {
                TargetName = target.Name,
                OperationType = HistoryOperationType.Verify,
                Status = result.Success
                    ? HistoryOperationStatus.Succeeded
                    : HistoryOperationStatus.Failed,
                StartedAt = completedAt - result.Duration,
                UpdatedAt = completedAt,
                CompletedAt = completedAt,
                FileName = Path.GetFileName(backupFilePath),
                FileSizeBytes = File.Exists(backupFilePath) ? new FileInfo(backupFilePath).Length : 0,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                ErrorMessage = result.ErrorMessage
            };
            await _historyService.AddRecordAsync(record, cancellationToken);
        }
        catch
        {
            // A history write failure must not mask the verification result.
        }
    }

    private static SqlServerBackupKind GetSqlServerBackupKind(string path)
    {
        var name = path.EndsWith(".age", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        if (name.EndsWith(".log.bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".log.bak.gz", StringComparison.OrdinalIgnoreCase))
            return SqlServerBackupKind.Log;
        if (name.EndsWith(".diff.bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".diff.bak.gz", StringComparison.OrdinalIgnoreCase))
            return SqlServerBackupKind.Differential;
        return SqlServerBackupKind.Full;
    }

    private static RestoreSource DescribeSource(string backupFilePath)
    {
        EnsureNotEncrypted(backupFilePath);
        var isCompressed = backupFilePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var fileName = Path.GetFileName(backupFilePath);
        var extension = isCompressed
            ? Path.GetExtension(fileName[..^3])
            : Path.GetExtension(fileName);

        return new RestoreSource(backupFilePath, extension, isCompressed);
    }

    private static void EnsureNotEncrypted(string filePath)
    {
        if (filePath.EndsWith(".age", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This backup is age-encrypted. Decrypt it with 'age --decrypt --identity <key> --output <file> <file.age>' before restore or test-restore.");
    }

    private static Stream OpenSourceStream(RestoreSource source)
    {
        var fileStream = new FileStream(
            source.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return source.IsCompressed
            ? new GZipStream(fileStream, CompressionMode.Decompress)
            : fileStream;
    }

    private static async Task<string> ReadOutputAsync(ProcessResult result, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(result.OutputStream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string? BuildValidationDetails(DatabaseType type, string? validationOutput)
    {
        var firstLine = validationOutput?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(firstLine))
            return null;

        return type switch
        {
            DatabaseType.PostgreSql => $"Restored database contains {firstLine} user tables.",
            DatabaseType.SqlServer => $"Restored database contains {firstLine} tables.",
            DatabaseType.MongoDb => $"Restored database contains {firstLine} collections.",
            DatabaseType.SQLite => $"SQLite integrity check: {firstLine}.",
            _ => firstLine
        };
    }
}

using System.Diagnostics;
using System.IO.Compression;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;

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
        DatabaseTarget target,
        string backupFilePath,
        VerifyResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = new BackupRecord
            {
                TargetName = target.Name,
                Timestamp = DateTime.UtcNow,
                FileName = Path.GetFileName(backupFilePath),
                FileSizeBytes = File.Exists(backupFilePath) ? new FileInfo(backupFilePath).Length : 0,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                Success = result.Success,
                ErrorMessage = result.ErrorMessage,
                RecordType = "verify"
            };
            await _historyService.AddRecordAsync(record, cancellationToken);
        }
        catch
        {
            // A history write failure must not mask the verification result.
        }
    }

    private static RestoreSource DescribeSource(string backupFilePath)
    {
        var isCompressed = backupFilePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var fileName = Path.GetFileName(backupFilePath);
        var extension = isCompressed
            ? Path.GetExtension(fileName[..^3])
            : Path.GetExtension(fileName);

        return new RestoreSource(backupFilePath, extension, isCompressed);
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
            _ => firstLine
        };
    }
}

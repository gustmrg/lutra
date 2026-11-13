using System.Diagnostics;
using System.IO.Compression;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;
using Lutra.Core.Postgres;

namespace Lutra.Core.Restore;

/// <summary>
/// Coordinates destructive restores and non-destructive test-restores (verification)
/// of database backups produced by <see cref="BackupOrchestrator"/>.
/// </summary>
public class RestoreOrchestrator
{
    private readonly IProcessExecutor _processExecutor;
    private readonly IBackupHistoryService _historyService;
    private readonly BackupConfig _config;

    public RestoreOrchestrator(
        IProcessExecutor processExecutor,
        IBackupHistoryService historyService,
        BackupConfig config)
    {
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

            var source = DescribeSource(backupFilePath);

            await RestoreIntoAsync(target, target.Database, source, cancellationToken);

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
        string? testDatabase = null;
        await using var operation = await HistoryOperationScope.BeginAsync(
            _historyService,
            target.Name,
            HistoryOperationType.Verify,
            cancellationToken);

        try
        {
            if (!File.Exists(backupFilePath))
                throw new FileNotFoundException($"Backup file not found: {backupFilePath}");

            var integrity = await BackupIntegrity.VerifyFileAsync(backupFilePath, cancellationToken);
            if (!integrity.Success)
                throw new InvalidOperationException($"Integrity check failed: {integrity.Message}");
            checksumValid = true;

            await using var targetLock = TargetLock.Acquire(_config.BackupDirectory, target.Name, "Verify");

            var source = DescribeSource(backupFilePath);
            testDatabase = PostgresCommands.TestDatabaseName(restoreId);

            await ExecuteCheckedAsync(
                PostgresCommands.CreateDatabase(target, testDatabase),
                "Failed to create temporary database",
                cancellationToken);

            string? validationOutput;
            try
            {
                await RestoreIntoAsync(target, testDatabase, source, cancellationToken);

                using var validation = await _processExecutor.ExecuteAsync(
                    PostgresCommands.CountUserTables(target, testDatabase), cancellationToken);
                if (!validation.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Validation query failed (exit code {validation.ExitCode}): {validation.StandardError}");
                }
                validationOutput = await ReadOutputAsync(validation, cancellationToken);
            }
            finally
            {
                await TryDropDatabaseAsync(target, testDatabase);
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
                ValidationDetails = BuildValidationDetails(validationOutput)
            };
            await TryCompleteVerificationAsync(operation, backupFilePath, successResult);
            return successResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            if (testDatabase is not null)
                await TryDropDatabaseAsync(target, testDatabase);

            var failureResult = new VerifyResult
            {
                TargetName = target.Name,
                BackupFilePath = backupFilePath,
                ChecksumValid = checksumValid,
                Success = false,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
            await TryFinishFailedVerificationAsync(
                operation, failureResult, ex, cancellationToken.IsCancellationRequested);
            return failureResult;
        }
    }

    private async Task RestoreIntoAsync(
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source,
        CancellationToken cancellationToken)
    {
        await using var input = OpenSourceStream(source);

        if (destinationDatabase.Equals(target.Database, StringComparison.Ordinal))
        {
            foreach (var prepareCommand in PostgresCommands.PrepareDestructiveRestore(target, source))
                await ExecuteCheckedAsync(prepareCommand, "Failed to prepare database for restore", cancellationToken);
        }

        var command = PostgresCommands.Restore(target, destinationDatabase, source);
        using var result = await _processExecutor.ExecuteWithInputAsync(command, input, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Restore command failed (exit code {result.ExitCode}): {result.StandardError}");
        }
    }

    private async Task ExecuteCheckedAsync(
        DockerExecCommand command, string errorPrefix, CancellationToken cancellationToken)
    {
        using var result = await _processExecutor.ExecuteAsync(command, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"{errorPrefix} (exit code {result.ExitCode}): {result.StandardError}");
    }

    private async Task TryDropDatabaseAsync(DatabaseTarget target, string database)
    {
        try
        {
            using var result = await _processExecutor.ExecuteAsync(
                PostgresCommands.DropDatabase(target, database), CancellationToken.None);
        }
        catch
        {
            // Best-effort cleanup of the temporary database; verification already
            // has its result at this point.
        }
    }

    private static async Task TryCompleteVerificationAsync(
        HistoryOperationScope operation,
        string backupFilePath,
        VerifyResult result)
    {
        try
        {
            await operation.CompleteAsync(new HistoryOperationCompletion(
                FileName: Path.GetFileName(backupFilePath),
                FileSizeBytes: File.Exists(backupFilePath) ? new FileInfo(backupFilePath).Length : 0,
                DurationMs: (long)result.Duration.TotalMilliseconds));
        }
        catch
        {
            // A history write failure must not mask the verification result.
        }
    }

    private static async Task TryFinishFailedVerificationAsync(
        HistoryOperationScope operation,
        VerifyResult result,
        Exception exception,
        bool callerCancelled)
    {
        try
        {
            if (exception is OperationCanceledException || callerCancelled)
                await operation.CancelAsync(exception.Message, (long)result.Duration.TotalMilliseconds);
            else
                await operation.FailAsync(exception.Message, (long)result.Duration.TotalMilliseconds);
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

    private static string? BuildValidationDetails(string? validationOutput)
    {
        var firstLine = validationOutput?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(firstLine))
            return null;

        return $"Restored database contains {firstLine} user tables.";
    }
}

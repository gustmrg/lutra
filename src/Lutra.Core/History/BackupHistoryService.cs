using System.Collections.Concurrent;
using System.Text.Json;

namespace Lutra.Core.History;

public class BackupHistoryService : IBackupHistoryService
{
    private readonly string _historyFilePath;
    private readonly string _lockFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.Ordinal);

    public BackupHistoryService(string backupDirectory)
    {
        _historyFilePath = Path.Combine(backupDirectory, "backup-history.json");
        _lockFilePath = Path.Combine(backupDirectory, ".backup-history.lock");
    }

    public async Task AddRecordAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        await WithHistoryLockAsync(async () =>
        {
            var records = await LoadRecordsAsync(cancellationToken);
            records.Add(record);
            await SaveRecordsAsync(records, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetAllRecordsAsync(CancellationToken cancellationToken = default)
    {
        var records = await WithHistoryLockAsync(
            () => LoadRecordsAsync(cancellationToken),
            cancellationToken);

        return records.OrderByDescending(r => r.Timestamp).ToList();
    }

    public async Task<IReadOnlyList<BackupRecord>> GetRecordsByTargetAsync(string targetName, CancellationToken cancellationToken = default)
    {
        var records = await WithHistoryLockAsync(
            () => LoadRecordsAsync(cancellationToken),
            cancellationToken);

        return records
            .Where(r => r.TargetName == targetName)
            .OrderByDescending(r => r.Timestamp)
            .ToList();
    }

    public async Task<bool> RemoveRecordAsync(string targetName, string fileName, CancellationToken cancellationToken = default)
    {
        return await WithHistoryLockAsync(async () =>
        {
            var records = await LoadRecordsAsync(cancellationToken);
            var removed = records.RemoveAll(r => r.TargetName == targetName && r.FileName == fileName);

            if (removed > 0)
                await SaveRecordsAsync(records, cancellationToken);

            return removed > 0;
        }, cancellationToken);
    }

    public async Task<int> PruneOperationalRecordsAsync(
        DateTime olderThan,
        CancellationToken cancellationToken = default)
    {
        return await WithHistoryLockAsync(async () =>
        {
            var records = await LoadRecordsAsync(cancellationToken);
            var removed = records.RemoveAll(record =>
                record.Timestamp < olderThan && (!record.Success || record.RecordType is not null));
            if (removed > 0)
                await SaveRecordsAsync(records, cancellationToken);
            return removed;
        }, cancellationToken);
    }

    private async Task<List<BackupRecord>> LoadRecordsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyFilePath))
            return [];

        await using var stream = File.OpenRead(_historyFilePath);
        return await JsonSerializer.DeserializeAsync<List<BackupRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveRecordsAsync(List<BackupRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_historyFilePath)!;
        Directory.CreateDirectory(directory);

        var tempPath = $"{_historyFilePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _historyFilePath, overwrite: true);
    }

    private async Task<T> WithHistoryLockAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_historyFilePath)!;
        Directory.CreateDirectory(directory);

        var processLock = ProcessLocks.GetOrAdd(_lockFilePath, _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken);
        try
        {
            await using var lockStream = await AcquireHistoryLockAsync(cancellationToken);
            return await action();
        }
        finally
        {
            processLock.Release();
        }
    }

    private async Task<FileStream> AcquireHistoryLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                1,
                useAsync: false);
            try
            {
                LockFile(stream);
                return stream;
            }
            catch (IOException)
            {
                await stream.DisposeAsync();
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static void LockFile(FileStream stream)
    {
        if (!OperatingSystem.IsMacOS())
            stream.Lock(0, 0);
    }
}

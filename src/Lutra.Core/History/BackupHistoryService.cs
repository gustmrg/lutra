using System.Text.Json;

namespace Lutra.Core.History;

public class BackupHistoryService : IBackupHistoryService
{
    private readonly string _historyFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public BackupHistoryService(string backupDirectory)
    {
        _historyFilePath = Path.Combine(backupDirectory, "backup-history.json");
    }

    public async Task AddRecordAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        var records = await LoadRecordsAsync(cancellationToken);
        records.Add(record);
        await SaveRecordsAsync(records, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupRecord>> GetAllRecordsAsync(CancellationToken cancellationToken = default)
    {
        var records = await LoadRecordsAsync(cancellationToken);
        return records.OrderByDescending(r => r.Timestamp).ToList();
    }

    public async Task<IReadOnlyList<BackupRecord>> GetRecordsByTargetAsync(string targetName, CancellationToken cancellationToken = default)
    {
        var records = await LoadRecordsAsync(cancellationToken);
        return records
            .Where(r => r.TargetName == targetName)
            .OrderByDescending(r => r.Timestamp)
            .ToList();
    }

    public async Task<bool> RemoveRecordAsync(string targetName, string fileName, CancellationToken cancellationToken = default)
    {
        var records = await LoadRecordsAsync(cancellationToken);
        var removed = records.RemoveAll(r => r.TargetName == targetName && r.FileName == fileName);

        if (removed > 0)
            await SaveRecordsAsync(records, cancellationToken);

        return removed > 0;
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

        var tempPath = _historyFilePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _historyFilePath, overwrite: true);
    }
}

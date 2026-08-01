using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Backup;

public sealed class OrphanCleanupService
{
    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;

    public OrphanCleanupService(BackupConfig config, IBackupHistoryService history)
    {
        _config = config;
        _history = history;
    }

    public async Task<IReadOnlyList<OrphanCandidate>> FindAsync(
        IBackupTarget target,
        bool includeUntrackedBackups,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_config.BackupDirectory, target.Name);
        if (!Directory.Exists(directory))
            return [];

        var candidates = new List<OrphanCandidate>();
        foreach (var sidecar in Directory.EnumerateFiles(directory)
                     .Where(path => path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var backupPath = sidecar.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                ? sidecar[..^".sha256".Length]
                : sidecar[..^".json".Length];
            if (!File.Exists(backupPath))
                candidates.Add(new OrphanCandidate(OrphanKind.SidecarWithoutBackup, [sidecar]));
        }

        if (includeUntrackedBackups)
        {
            var records = await _history.GetRecordsByTargetAsync(target.Name, cancellationToken);
            var tracked = records
                .Where(record => record.Status == HistoryOperationStatus.Succeeded
                    && record.OperationType == HistoryOperationType.Backup
                    && !string.IsNullOrWhiteSpace(record.FileName))
                .Select(record => record.FileName!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                    || tracked.Contains(name))
                    continue;

                candidates.Add(new OrphanCandidate(
                    OrphanKind.BackupWithoutHistory,
                    [path, BackupIntegrity.GetChecksumPath(path), BackupIntegrity.GetManifestPath(path)]));
            }
        }

        return candidates;
    }

    public static int Delete(IEnumerable<OrphanCandidate> candidates)
    {
        var deleted = 0;
        foreach (var candidate in candidates)
        {
            foreach (var path in candidate.Paths)
            {
                if (!File.Exists(path))
                    continue;
                File.Delete(path);
                deleted++;
            }
        }
        return deleted;
    }
}

public enum OrphanKind
{
    SidecarWithoutBackup,
    BackupWithoutHistory
}

public sealed record OrphanCandidate(OrphanKind Kind, IReadOnlyList<string> Paths);

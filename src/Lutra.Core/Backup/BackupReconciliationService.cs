using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Backup;

/// <summary>
/// Compares backup artifacts on disk with successful backup history records.
/// This service is deliberately read-only.
/// </summary>
public sealed class BackupReconciliationService
{
    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;

    public BackupReconciliationService(BackupConfig config, IBackupHistoryService history)
    {
        _config = config;
        _history = history;
    }

    public async Task<BackupReconciliationReport> ReconcileAsync(
        string? targetName = null,
        CancellationToken cancellationToken = default)
    {
        var configuredTargets = _config.AllTargets().ToList();
        var walTargets = _config.Databases
            .Where(database => database.PostgresWalArchivePath is not null)
            .Select(database => (IBackupTarget)new RecoveryArtifactTarget
            {
                Name = database.Name + "-wal",
                Schedule = database.Schedule,
                Compression = database.Compression,
                Retention = database.Retention,
                Encryption = database.Encryption
            })
            .ToList();
        var targets = targetName is null
            ? configuredTargets.Concat(walTargets).ToList()
            : configuredTargets
                .Where(target => target.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                .Concat(_config.Databases.Any(database => database.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    ? walTargets.Where(target => target.Name.Equals(targetName + "-wal", StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Empty<IBackupTarget>())
                .ToList();

        if (targetName is not null && targets.Count == 0)
            throw new ConfigurationException($"Target '{targetName}' not found.");

        var allRecords = await _history.GetAllRecordsAsync(cancellationToken);
        var findings = new List<BackupReconciliationFinding>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetDirectory = Path.Combine(_config.BackupDirectory, target.Name);
            var records = allRecords
                .Where(record => record.Success
                    && record.RecordType is null
                    && record.TargetName.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(record.FileName))
                .ToList();
            var trackedNames = records.Select(record => record.FileName).ToHashSet(StringComparer.Ordinal);

            if (Directory.Exists(targetDirectory))
            {
                foreach (var filePath in Directory.EnumerateFiles(targetDirectory))
                {
                    var fileName = Path.GetFileName(filePath);
                    if (IsSidecarOrTemporary(fileName) || trackedNames.Contains(fileName))
                        continue;

                    findings.Add(new BackupReconciliationFinding(
                        target.Name,
                        ReconciliationFindingType.FileWithoutHistory,
                        filePath,
                        "Backup file has no successful history entry."));
                }
            }

            foreach (var record in records)
            {
                var backupPath = Path.Combine(targetDirectory, record.FileName);
                if (!File.Exists(backupPath))
                {
                    findings.Add(new BackupReconciliationFinding(
                        target.Name,
                        ReconciliationFindingType.HistoryWithoutFile,
                        backupPath,
                        "Successful history entry references a missing backup file."));
                    continue;
                }

                var checksumPath = BackupIntegrity.GetChecksumPath(backupPath);
                if (!File.Exists(checksumPath))
                {
                    findings.Add(new BackupReconciliationFinding(
                        target.Name,
                        ReconciliationFindingType.MissingChecksum,
                        checksumPath,
                        "Backup file is missing its checksum sidecar."));
                }

                var manifestPath = BackupIntegrity.GetManifestPath(backupPath);
                if (!File.Exists(manifestPath))
                {
                    findings.Add(new BackupReconciliationFinding(
                        target.Name,
                        ReconciliationFindingType.MissingManifest,
                        manifestPath,
                        "Backup file is missing its manifest sidecar."));
                }
            }
        }

        return new BackupReconciliationReport(targets.Count, findings);
    }

    private static bool IsSidecarOrTemporary(string fileName)
        => fileName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith('.');
}

public enum ReconciliationFindingType
{
    FileWithoutHistory,
    HistoryWithoutFile,
    MissingChecksum,
    MissingManifest
}

public sealed record BackupReconciliationFinding(
    string TargetName,
    ReconciliationFindingType Type,
    string Path,
    string Message);

public sealed record BackupReconciliationReport(
    int TargetsChecked,
    IReadOnlyList<BackupReconciliationFinding> Findings)
{
    public bool IsClean => Findings.Count == 0;
}

using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using System.Text.Json;
using Lutra.Core.History;
using Lutra.Core.Sync;

namespace Lutra.Core.Health;

/// <summary>Checks that successful history records still have valid local artifacts.</summary>
public sealed class BackupArtifactHealthChecker
{
    private readonly BackupConfig _config;
    private readonly IBackupHistoryService _history;

    public BackupArtifactHealthChecker(BackupConfig config, IBackupHistoryService history)
    {
        _config = config;
        _history = history;
    }

    public async Task<IReadOnlyList<HealthFinding>> CheckAsync(
        IBackupTarget target,
        IReadOnlyList<BackupRecord> records,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<HealthFinding>();
        var successful = records
            .Where(record => record.Success && record.RecordType is null && !string.IsNullOrWhiteSpace(record.FileName))
            .OrderByDescending(record => record.Timestamp)
            .ToList();

        foreach (var record in successful)
        {
            var path = Path.Combine(_config.BackupDirectory, target.Name, record.FileName);
            if (File.Exists(path))
                continue;

            findings.Add(new HealthFinding
            {
                Type = FindingType.MissingFile,
                Severity = Severity.Critical,
                Message = "A successful history entry references a missing backup file.",
                Detail = record.FileName,
                RelevantTimestamp = record.Timestamp
            });
        }

        var latestExisting = successful.FirstOrDefault(record =>
            File.Exists(Path.Combine(_config.BackupDirectory, target.Name, record.FileName)));
        if (latestExisting is not null)
        {
            var path = Path.Combine(_config.BackupDirectory, target.Name, latestExisting.FileName);
            var verification = await BackupIntegrity.VerifyFileAsync(path, cancellationToken);
            if (!verification.Success)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.IntegrityFailure,
                    Severity = Severity.Critical,
                    Message = "Latest backup failed its local integrity check.",
                    Detail = verification.Message,
                    RelevantTimestamp = latestExisting.Timestamp
                });
            }
        }

        if (target is DatabaseTarget { Type: DatabaseType.PostgreSql, PostgresWalArchivePath: not null } database)
        {
            var walName = database.Name + "-wal";
            var walRecords = await _history.GetRecordsByTargetAsync(walName, cancellationToken);
            var latestWal = walRecords.FirstOrDefault(record => record.Success && record.RecordType is null);
            if (latestWal is null)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.MissingFile,
                    Severity = Severity.Warning,
                    Message = "No successful archived-WAL artifact exists for this PostgreSQL target."
                });
            }
            else
            {
                var walPath = Path.Combine(_config.BackupDirectory, walName, latestWal.FileName);
                var integrity = await BackupIntegrity.VerifyFileAsync(walPath, cancellationToken);
                if (!integrity.Success)
                {
                    findings.Add(new HealthFinding
                    {
                        Type = FindingType.IntegrityFailure,
                        Severity = Severity.Critical,
                        Message = "Latest archived-WAL artifact failed its integrity check.",
                        Detail = integrity.Message,
                        RelevantTimestamp = latestWal.Timestamp
                    });
                }
            }
        }

        if (_config.Sync is { PostBackup: true } && successful.Count > 0)
        {
            var markers = new[]
                {
                    Path.Combine(_config.BackupDirectory, target.Name, ".last-sync.json"),
                    Path.Combine(_config.BackupDirectory, ".last-sync.json")
                }
                .Where(File.Exists)
                .ToList();
            var latestSync = await ReadLatestSuccessfulSyncAsync(markers, cancellationToken);
            if (latestSync is null || latestSync.StartedAt < successful[0].Timestamp)
            {
                findings.Add(new HealthFinding
                {
                    Type = FindingType.MissingOffsiteSync,
                    Severity = Severity.Warning,
                    Message = "The latest successful backup has no successful offsite sync marker.",
                    RelevantTimestamp = successful[0].Timestamp
                });
            }
        }

        return findings;
    }

    private static async Task<SyncResult?> ReadLatestSuccessfulSyncAsync(
        IEnumerable<string> markerPaths,
        CancellationToken cancellationToken)
    {
        var results = new List<SyncResult>();
        foreach (var markerPath in markerPaths)
        {
            try
            {
                await using var stream = File.OpenRead(markerPath);
                var result = await JsonSerializer.DeserializeAsync<SyncResult>(stream, cancellationToken: cancellationToken);
                if (result is { Success: true })
                    results.Add(result);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Invalid markers are treated as missing.
            }
        }
        return results.OrderByDescending(result => result.StartedAt).FirstOrDefault();
    }
}

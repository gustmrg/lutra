using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.Core.Health;

/// <summary>Checks that successful history records still have valid local artifacts.</summary>
public sealed class BackupArtifactHealthChecker
{
    private readonly string _backupDirectory;

    public BackupArtifactHealthChecker(string backupDirectory)
    {
        _backupDirectory = backupDirectory;
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
            var path = Path.Combine(_backupDirectory, target.Name, record.FileName);
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
            File.Exists(Path.Combine(_backupDirectory, target.Name, record.FileName)));
        if (latestExisting is not null)
        {
            var path = Path.Combine(_backupDirectory, target.Name, latestExisting.FileName);
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

        return findings;
    }
}

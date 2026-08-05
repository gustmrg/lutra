using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Notifications;

public static class BackupNotificationMapper
{
    public static IReadOnlyList<BackupNotificationDetail> Map(
        IReadOnlyList<BackupResult> results,
        BackupConfig config)
    {
        return results.Select(result => Map(result, config)).ToList();
    }

    public static BackupNotificationDetail Map(BackupResult result, BackupConfig config)
    {
        var target = config.AllTargets().FirstOrDefault(candidate =>
            candidate.Name.Equals(result.TargetName, StringComparison.OrdinalIgnoreCase));
        var walDatabase = target is null
            ? config.Databases.FirstOrDefault(database =>
                (database.Name + "-wal").Equals(result.TargetName, StringComparison.OrdinalIgnoreCase))
            : null;

        if (target is null && walDatabase is null)
            throw new InvalidOperationException($"Backup result target '{result.TargetName}' is not configured.");

        var database = target as DatabaseTarget ?? walDatabase;
        var targetKind = walDatabase is not null
            ? BackupNotificationTargetKind.PostgresWal
            : target switch
            {
                DatabaseTarget => BackupNotificationTargetKind.Database,
                FileTarget => BackupNotificationTargetKind.Files,
                VolumeTarget => BackupNotificationTargetKind.Volume,
                _ => throw new InvalidOperationException($"Unsupported backup target '{result.TargetName}'.")
            };

        return new BackupNotificationDetail
        {
            TargetName = result.TargetName,
            TargetKind = targetKind,
            Success = result.Success,
            Database = database?.Database,
            Container = database?.Container,
            FileName = string.IsNullOrWhiteSpace(result.FilePath) ? null : Path.GetFileName(result.FilePath),
            FileSizeBytes = result.FileSizeBytes,
            Destination = string.IsNullOrWhiteSpace(result.FilePath) ? null : Path.GetDirectoryName(result.FilePath),
            Duration = result.Duration,
            ErrorMessage = result.Success
                ? null
                : string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Unknown error" : result.ErrorMessage
        };
    }
}

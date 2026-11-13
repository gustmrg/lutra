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
        var target = config.Databases.FirstOrDefault(candidate =>
            candidate.Name.Equals(result.TargetName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            throw new InvalidOperationException($"Backup result target '{result.TargetName}' is not configured.");


        return new BackupNotificationDetail
        {
            TargetName = result.TargetName,
            Success = result.Success,
            Database = target.Database,
            Container = target.Container,
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

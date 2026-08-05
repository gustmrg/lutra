namespace Lutra.Core.Notifications;

public enum NotificationStatus
{
    Success,
    Failure
}

public sealed record NotificationEvent
{
    public required string Name { get; init; }
    public required NotificationStatus Status { get; init; }
    public required string Summary { get; init; }
    public string? TargetName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Host { get; init; }
    public IReadOnlyList<BackupNotificationDetail> Backups { get; init; } = [];

    public static NotificationEvent Create(
        string name,
        bool success,
        string summary,
        string? targetName = null,
        IReadOnlyList<BackupNotificationDetail>? backups = null)
    {
        return new NotificationEvent
        {
            Name = name,
            Status = success ? NotificationStatus.Success : NotificationStatus.Failure,
            Summary = summary,
            TargetName = targetName,
            Timestamp = DateTimeOffset.UtcNow,
            Host = Environment.MachineName,
            Backups = backups ?? []
        };
    }
}

public enum BackupNotificationTargetKind
{
    Database,
    Files,
    Volume,
    PostgresWal
}

public sealed record BackupNotificationDetail
{
    public required string TargetName { get; init; }
    public required BackupNotificationTargetKind TargetKind { get; init; }
    public required bool Success { get; init; }
    public string? Database { get; init; }
    public string? Container { get; init; }
    public string? FileName { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? Destination { get; init; }
    public required TimeSpan Duration { get; init; }
    public string? ErrorMessage { get; init; }
}

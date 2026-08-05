namespace Lutra.Core.Notifications;

public interface INotificationChannel
{
    string Name { get; }

    Task<IReadOnlyList<string>> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default);
}

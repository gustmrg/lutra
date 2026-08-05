namespace Lutra.Core.Notifications;

/// <summary>Dispatches best-effort notifications without affecting operation results.</summary>
public sealed class NotificationService
{
    private readonly IReadOnlyList<INotificationChannel> _channels;

    public NotificationService(IEnumerable<INotificationChannel> channels)
    {
        _channels = channels.ToList();
    }

    public async Task<IReadOnlyList<string>> NotifyAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        foreach (var channel in _channels)
        {
            try
            {
                errors.AddRange(await channel.SendAsync(notification, cancellationToken));
            }
            catch (Exception ex)
            {
                errors.Add($"{channel.Name} notification failed: {ex.GetType().Name}.");
            }
        }

        return errors;
    }
}

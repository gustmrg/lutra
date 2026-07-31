namespace Lutra.Core.Notifications;

/// <summary>Lightweight outbound notification configuration.</summary>
public sealed class NotificationConfig
{
    /// <summary>Generic webhook endpoints that receive JSON POST requests.</summary>
    public List<string> Webhooks { get; init; } = [];

    /// <summary>
    /// Optional Healthchecks.io-compatible ping URL. Successful operations ping the
    /// URL; failures ping the same URL with <c>/fail</c> appended.
    /// </summary>
    public string? HealthchecksUrl { get; init; }
}

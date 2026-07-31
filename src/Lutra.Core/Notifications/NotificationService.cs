using System.Net.Http.Json;

namespace Lutra.Core.Notifications;

/// <summary>Sends best-effort webhook and Healthchecks.io-compatible notifications.</summary>
public sealed class NotificationService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly NotificationConfig _config;

    public NotificationService(NotificationConfig config)
    {
        _config = config;
    }

    public async Task<IReadOnlyList<string>> NotifyAsync(
        string eventName,
        bool success,
        string summary,
        string? targetName = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var payload = new NotificationPayload(
            eventName,
            success ? "success" : "failure",
            summary,
            targetName,
            DateTime.UtcNow,
            Environment.MachineName);

        foreach (var webhook in _config.Webhooks)
        {
            try
            {
                using var response = await HttpClient.PostAsJsonAsync(webhook, payload, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    errors.Add($"Webhook returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors.Add($"Webhook failed: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_config.HealthchecksUrl))
        {
            try
            {
                var url = success
                    ? _config.HealthchecksUrl
                    : _config.HealthchecksUrl.TrimEnd('/') + "/fail";
                using var response = await HttpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    errors.Add($"Healthchecks.io ping returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                errors.Add($"Healthchecks.io ping failed: {ex.Message}");
            }
        }

        return errors;
    }
}

public sealed record NotificationPayload(
    string Event,
    string Status,
    string Summary,
    string? Target,
    DateTime Timestamp,
    string Host);

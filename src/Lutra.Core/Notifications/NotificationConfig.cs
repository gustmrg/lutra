using Lutra.Core.Configuration;

namespace Lutra.Core.Notifications;

/// <summary>Optional outbound notification configuration.</summary>
public sealed class NotificationConfig
{
    public DiscordNotificationConfig? Discord { get; init; }
}

public sealed class DiscordNotificationConfig
{
    public List<DiscordWebhookConfig> Webhooks { get; init; } = [];
}

public sealed class DiscordWebhookConfig
{
    /// <summary>Name of the environment variable containing the Discord webhook URL.</summary>
    public string UrlEnv { get; init; } = "";
}

public static class DiscordWebhookUrlResolver
{
    public static IReadOnlyList<Uri> Resolve(DiscordNotificationConfig config)
    {
        if (config.Webhooks is not { Count: > 0 } webhooks)
            throw new ConfigurationException("notifications.discord: at least one webhook is required.");

        var urls = new List<Uri>(webhooks.Count);
        for (var i = 0; i < webhooks.Count; i++)
        {
            var prefix = $"notifications.discord.webhooks[{i}]";
            var webhook = webhooks[i];
            if (webhook is null)
                throw new ConfigurationException($"{prefix}: a webhook entry is required.");

            var variableName = webhook.UrlEnv;
            if (string.IsNullOrWhiteSpace(variableName))
                throw new ConfigurationException($"{prefix}: 'url_env' is required.");

            var value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConfigurationException(
                    $"{prefix}: url_env '{variableName}' is not set in the environment or .env file.");
            }

            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !IsDiscordHost(uri.Host))
            {
                throw new ConfigurationException(
                    $"{prefix}: url_env '{variableName}' must contain an HTTPS Discord webhook URL.");
            }

            urls.Add(uri);
        }

        return urls;
    }

    private static bool IsDiscordHost(string host)
    {
        return host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
    }
}

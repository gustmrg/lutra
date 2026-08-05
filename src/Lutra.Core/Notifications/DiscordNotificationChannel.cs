using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lutra.Core.Notifications;

public sealed class DiscordNotificationChannel : INotificationChannel
{
    private const int SuccessColor = 0x2ECC71;
    private const int FailureColor = 0xE74C3C;
    private const int MaxEmbeds = 10;
    private const int MaxEmbedCharacters = 6000;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<Uri> _webhookUrls;

    public DiscordNotificationChannel(HttpClient httpClient, IEnumerable<Uri> webhookUrls)
    {
        _httpClient = httpClient;
        _webhookUrls = webhookUrls.ToList();
    }

    public string Name => "Discord";

    public async Task<IReadOnlyList<string>> SendAsync(
        NotificationEvent notification,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var embeds = notification.Backups.Count > 0
            ? notification.Backups.Select(detail => BuildBackupEmbed(detail, notification)).ToList()
            : [BuildOperationEmbed(notification)];
        var chunks = ChunkEmbeds(embeds);

        for (var webhookIndex = 0; webhookIndex < _webhookUrls.Count; webhookIndex++)
        {
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                try
                {
                    var payload = new DiscordWebhookPayload(
                        "Lutra",
                        chunks[chunkIndex],
                        new DiscordAllowedMentions([]));
                    using var response = await _httpClient.PostAsJsonAsync(
                        _webhookUrls[webhookIndex], payload, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add(
                            $"Discord webhook {webhookIndex + 1}, request {chunkIndex + 1} returned HTTP {(int)response.StatusCode}.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"Discord webhook {webhookIndex + 1}, request {chunkIndex + 1} failed: {ex.GetType().Name}.");
                }
            }
        }

        return errors;
    }

    private static DiscordEmbed BuildBackupEmbed(
        BackupNotificationDetail detail,
        NotificationEvent notification)
    {
        var fields = new List<DiscordEmbedField>
        {
            Field("Target", detail.TargetName, true, 256)
        };

        if (!string.IsNullOrWhiteSpace(detail.Database))
            fields.Add(Field("Database", detail.Database, true, 256));

        if (detail.Success)
        {
            if (!string.IsNullOrWhiteSpace(detail.FileName))
                fields.Add(Field("Dump / archive", detail.FileName));
            if (detail.FileSizeBytes.HasValue)
                fields.Add(Field("Size", FormatBytes(detail.FileSizeBytes.Value), true));
            if (!string.IsNullOrWhiteSpace(detail.Destination))
                fields.Add(Field("Destination", detail.Destination));
        }
        else
        {
            fields.Add(Field("Error", detail.ErrorMessage ?? "Unknown error"));
            if (!string.IsNullOrWhiteSpace(detail.Container))
                fields.Add(Field("Container", detail.Container, true, 256));
        }

        fields.Add(Field("Duration", FormatDuration(detail.Duration), true));

        return new DiscordEmbed(
            detail.Success ? "\u2705 Lutra Backup - SUCCESS" : "\u274c Lutra Backup - FAILED",
            detail.Success ? SuccessColor : FailureColor,
            fields,
            notification.Timestamp,
            new DiscordEmbedFooter(Truncate($"Host: {notification.Host}", 256)));
    }

    private static DiscordEmbed BuildOperationEmbed(NotificationEvent notification)
    {
        var success = notification.Status == NotificationStatus.Success;
        var fields = new List<DiscordEmbedField>
        {
            Field("Summary", Truncate(notification.Summary, 3500))
        };
        if (!string.IsNullOrWhiteSpace(notification.TargetName))
            fields.Add(Field("Target", notification.TargetName, true, 256));

        return new DiscordEmbed(
            Truncate($"Lutra - {notification.Name.Replace('_', ' ').ToUpperInvariant()}", 256),
            success ? SuccessColor : FailureColor,
            fields,
            notification.Timestamp,
            new DiscordEmbedFooter(Truncate($"Host: {notification.Host}", 256)));
    }

    private static DiscordEmbedField Field(
        string name,
        string value,
        bool inline = false,
        int valueLimit = 1024)
        => new(Truncate(name, 256), Truncate(value, valueLimit), inline);

    private static List<IReadOnlyList<DiscordEmbed>> ChunkEmbeds(IReadOnlyList<DiscordEmbed> embeds)
    {
        var chunks = new List<IReadOnlyList<DiscordEmbed>>();
        var current = new List<DiscordEmbed>();
        var currentCharacters = 0;

        foreach (var embed in embeds)
        {
            var characters = CountCharacters(embed);
            if (current.Count > 0
                && (current.Count == MaxEmbeds || currentCharacters + characters > MaxEmbedCharacters))
            {
                chunks.Add(current);
                current = [];
                currentCharacters = 0;
            }

            current.Add(embed);
            currentCharacters += characters;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    private static int CountCharacters(DiscordEmbed embed)
    {
        return embed.Title.Length
            + embed.Footer.Text.Length
            + embed.Fields.Sum(field => field.Name.Length + field.Value.Length);
    }

    private static string Truncate(string value, int limit)
    {
        if (value.Length <= limit)
            return value;
        var contentLength = limit - 3;
        if (contentLength > 0 && char.IsHighSurrogate(value[contentLength - 1]))
            contentLength--;
        return value[..contentLength] + "...";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {suffixes[order]}";
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";

    private sealed record DiscordWebhookPayload(
        string Username,
        IReadOnlyList<DiscordEmbed> Embeds,
        [property: JsonPropertyName("allowed_mentions")] DiscordAllowedMentions AllowedMentions);

    private sealed record DiscordAllowedMentions(IReadOnlyList<string> Parse);

    private sealed record DiscordEmbed(
        string Title,
        int Color,
        IReadOnlyList<DiscordEmbedField> Fields,
        DateTimeOffset Timestamp,
        DiscordEmbedFooter Footer);

    private sealed record DiscordEmbedField(string Name, string Value, bool Inline);

    private sealed record DiscordEmbedFooter(string Text);
}

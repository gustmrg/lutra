using System.Net;
using System.Text.Json;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Notifications;

namespace Lutra.Core.Tests;

public sealed class NotificationTests
{
    [Fact]
    public async Task DiscordChannel_FormatsBackupResultsAndSuppressesMentions()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var channel = CreateChannel(handler, "https://discord.com/api/webhooks/1/token");
        var notification = CreateBackupEvent([
            new BackupNotificationDetail
            {
                TargetName = "postgres",
                TargetKind = BackupNotificationTargetKind.Database,
                Success = true,
                Database = "app",
                Container = "postgres-1",
                FileName = "postgres.dump.gz",
                FileSizeBytes = 247 * 1024 * 1024,
                Destination = "/var/backups/lutra/postgres",
                Duration = TimeSpan.FromSeconds(12)
            },
            new BackupNotificationDetail
            {
                TargetName = "mongo",
                TargetKind = BackupNotificationTargetKind.Database,
                Success = false,
                Database = "app",
                Container = "mongo-1",
                Duration = TimeSpan.FromSeconds(3),
                ErrorMessage = "Connection refused"
            }
        ]);

        var errors = await channel.SendAsync(notification);

        Assert.Empty(errors);
        var root = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.Equal("Lutra", root.GetProperty("username").GetString());
        Assert.Equal(0, root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        var embeds = root.GetProperty("embeds");
        Assert.Equal(2, embeds.GetArrayLength());
        Assert.Contains("SUCCESS", embeds[0].GetProperty("title").GetString());
        Assert.Contains("FAILED", embeds[1].GetProperty("title").GetString());
        AssertField(embeds[0], "Dump / archive", "postgres.dump.gz");
        AssertField(embeds[0], "Size", "247 MB");
        AssertField(embeds[0], "Destination", "/var/backups/lutra/postgres");
        AssertField(embeds[1], "Error", "Connection refused");
        AssertField(embeds[1], "Container", "mongo-1");
    }

    [Fact]
    public async Task DiscordChannel_ChunksEveryEndpointAndContinuesAfterFailure()
    {
        var handler = new RecordingHandler(index => new HttpResponseMessage(
            index == 0 ? HttpStatusCode.TooManyRequests : HttpStatusCode.NoContent));
        var channel = CreateChannel(
            handler,
            "https://discord.com/api/webhooks/1/token",
            "https://canary.discord.com/api/webhooks/2/token");
        var details = Enumerable.Range(1, 11).Select(index => new BackupNotificationDetail
        {
            TargetName = $"target-{index}",
            TargetKind = BackupNotificationTargetKind.Files,
            Success = true,
            FileName = $"target-{index}.tar.gz",
            FileSizeBytes = index,
            Destination = "/backups",
            Duration = TimeSpan.FromSeconds(1)
        }).ToList();

        var errors = await channel.SendAsync(CreateBackupEvent(details));

        Assert.Single(errors);
        Assert.Contains("HTTP 429", errors[0]);
        Assert.DoesNotContain("discord.com", errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, handler.Bodies.Count);
        Assert.Equal([10, 1, 10, 1], handler.Bodies
            .Select(body => JsonDocument.Parse(body).RootElement.GetProperty("embeds").GetArrayLength())
            .ToArray());
    }

    [Fact]
    public async Task DiscordChannel_TruncatesPayloadWithinCombinedEmbedLimit()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var channel = CreateChannel(handler, "https://discord.com/api/webhooks/1/token");
        var details = Enumerable.Range(1, 10).Select(index => new BackupNotificationDetail
        {
            TargetName = new string('t', 2000),
            TargetKind = BackupNotificationTargetKind.Database,
            Success = false,
            Database = new string('d', 2000),
            Container = new string('c', 2000),
            ErrorMessage = new string('e', 5000),
            Duration = TimeSpan.FromSeconds(index)
        }).ToList();

        var errors = await channel.SendAsync(CreateBackupEvent(details));

        Assert.Empty(errors);
        Assert.True(handler.Bodies.Count > 1);
        foreach (var body in handler.Bodies)
        {
            var embeds = JsonDocument.Parse(body).RootElement.GetProperty("embeds");
            Assert.InRange(embeds.GetArrayLength(), 1, 10);
            Assert.InRange(CountEmbedCharacters(embeds), 1, 6000);
            foreach (var embed in embeds.EnumerateArray())
            {
                Assert.InRange(embed.GetProperty("title").GetString()!.Length, 1, 256);
                foreach (var field in embed.GetProperty("fields").EnumerateArray())
                {
                    Assert.InRange(field.GetProperty("name").GetString()!.Length, 1, 256);
                    Assert.InRange(field.GetProperty("value").GetString()!.Length, 1, 1024);
                }
            }
        }
    }

    [Fact]
    public async Task NotificationService_IsolatesChannelsAndSanitizesUnexpectedErrors()
    {
        var failed = new StubChannel("first", _ => throw new InvalidOperationException("secret URL"));
        var succeeded = new StubChannel("second", _ => Task.FromResult<IReadOnlyList<string>>([]));
        var service = new NotificationService([failed, succeeded]);

        var errors = await service.NotifyAsync(NotificationEvent.Create("test", true, "ok"));

        Assert.True(failed.Called);
        Assert.True(succeeded.Called);
        var error = Assert.Single(errors);
        Assert.Contains("first", error);
        Assert.Contains(nameof(InvalidOperationException), error);
        Assert.DoesNotContain("secret", error);
    }

    [Fact]
    public async Task DiscordChannel_SanitizesTransportErrorsAndFormatsNonBackupEvents()
    {
        var handler = new RecordingHandler(_ =>
            throw new HttpRequestException("https://discord.com/api/webhooks/1/secret-token"));
        var channel = CreateChannel(handler, "https://discord.com/api/webhooks/1/secret-token");

        var errors = await channel.SendAsync(new NotificationEvent
        {
            Name = "verify_failure",
            Status = NotificationStatus.Failure,
            Summary = "Verification failed.",
            TargetName = "postgres",
            Timestamp = DateTimeOffset.UtcNow,
            Host = "lutra-host"
        });

        var error = Assert.Single(errors);
        Assert.Contains(nameof(HttpRequestException), error);
        Assert.DoesNotContain("secret-token", error);
        var embed = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement
            .GetProperty("embeds")[0];
        Assert.Contains("VERIFY FAILURE", embed.GetProperty("title").GetString());
        AssertField(embed, "Summary", "Verification failed.");
        AssertField(embed, "Target", "postgres");
    }

    [Fact]
    public void BackupMapper_MapsDatabaseWalAndBlankFailure()
    {
        var config = CreateConfig();
        config.Databases.Add(new DatabaseTarget
        {
            Name = "postgres",
            Type = DatabaseType.PostgreSql,
            Container = "postgres-1",
            Database = "app",
            Username = "postgres",
            PostgresWalArchivePath = "/wal"
        });
        var success = new BackupResult
        {
            TargetName = "POSTGRES-WAL",
            Success = true,
            Timestamp = DateTime.UtcNow,
            Duration = TimeSpan.FromSeconds(2),
            FilePath = "/backups/postgres-wal/archive.tar.gz",
            FileSizeBytes = 0
        };
        var failure = success with
        {
            TargetName = "postgres",
            Success = false,
            FilePath = null,
            FileSizeBytes = null,
            ErrorMessage = " "
        };

        var mapped = BackupNotificationMapper.Map([success, failure], config);

        Assert.Equal(BackupNotificationTargetKind.PostgresWal, mapped[0].TargetKind);
        Assert.Equal("app", mapped[0].Database);
        Assert.Equal("postgres-1", mapped[0].Container);
        Assert.Equal(0, mapped[0].FileSizeBytes);
        Assert.Equal("Unknown error", mapped[1].ErrorMessage);
    }

    [Fact]
    public void DiscordConfiguration_ResolvesEnvironmentAndRejectsUnsafeValuesWithoutLeakingThem()
    {
        using var temp = new TempDirectory();
        var variable = $"LUTRA_DISCORD_TEST_{Guid.NewGuid():N}";
        var configPath = WriteConfig(temp, variable);
        var previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, "https://discord.com/api/webhooks/1/secret-token");
            var config = new YamlConfigLoader().Load(configPath);
            Assert.Equal(variable, Assert.Single(config.Notifications!.Discord!.Webhooks).UrlEnv);

            const string unsafeValue = "https://discord.com.evil.example/api/webhooks/1/secret-token";
            Environment.SetEnvironmentVariable(variable, unsafeValue);
            var error = Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(configPath));
            Assert.Contains(variable, error.Message);
            Assert.DoesNotContain(unsafeValue, error.Message);
            Assert.DoesNotContain("secret-token", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void DiscordConfiguration_RejectsMissingEmptyAndLegacySettings()
    {
        using var temp = new TempDirectory();
        var missingVariable = $"LUTRA_DISCORD_MISSING_{Guid.NewGuid():N}";
        var missingPath = WriteConfig(temp, missingVariable);
        var missing = Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(missingPath));
        Assert.Contains(missingVariable, missing.Message);

        var emptyPath = Path.Combine(temp.Path, "empty.yaml");
        File.WriteAllText(emptyPath, BaseYaml(temp, "notifications:\n  discord:\n    webhooks: []"));
        Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(emptyPath));

        var nullCollectionPath = Path.Combine(temp.Path, "null-collection.yaml");
        File.WriteAllText(nullCollectionPath, BaseYaml(temp, "notifications:\n  discord:\n    webhooks:"));
        Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(nullCollectionPath));

        var nullEntryPath = Path.Combine(temp.Path, "null-entry.yaml");
        File.WriteAllText(nullEntryPath, BaseYaml(temp, "notifications:\n  discord:\n    webhooks:\n      -"));
        Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(nullEntryPath));

        var legacyPath = Path.Combine(temp.Path, "legacy.yaml");
        File.WriteAllText(legacyPath, BaseYaml(temp, "notifications:\n  healthchecks_url: https://hc-ping.com/test"));
        Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(legacyPath));

        var genericPath = Path.Combine(temp.Path, "generic.yaml");
        File.WriteAllText(genericPath, BaseYaml(temp, "notifications:\n  webhooks: [https://example.com/hook]"));
        Assert.Throws<ConfigurationException>(() => new YamlConfigLoader().Load(genericPath));

        var yamlTemplate = ConfigTemplates.GenerateYamlTemplate("/backups", "/state");
        Assert.Contains("url_env: LUTRA_DISCORD_WEBHOOK", yamlTemplate);
        Assert.DoesNotContain("healthchecks_url", yamlTemplate);
        Assert.DoesNotContain("notifications:\n#   webhooks", yamlTemplate);
    }

    private static DiscordNotificationChannel CreateChannel(RecordingHandler handler, params string[] urls)
        => new(new HttpClient(handler), urls.Select(url => new Uri(url)));

    private static NotificationEvent CreateBackupEvent(IReadOnlyList<BackupNotificationDetail> details)
        => new()
        {
            Name = details.All(detail => detail.Success) ? "backup_success" : "backup_failure",
            Status = details.All(detail => detail.Success)
                ? NotificationStatus.Success
                : NotificationStatus.Failure,
            Summary = "Backup run finished.",
            Timestamp = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
            Host = "lutra-host",
            Backups = details
        };

    private static BackupConfig CreateConfig() => new()
    {
        BackupDirectory = "/backups",
        Retention = new RetentionPolicy()
    };

    private static string WriteConfig(TempDirectory temp, string variable)
    {
        var path = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, BaseYaml(temp, $$"""
            notifications:
              discord:
                webhooks:
                  - url_env: {{variable}}
            """));
        return path;
    }

    private static string BaseYaml(TempDirectory temp, string extra) => $$"""
        backup_directory: {{temp.Path}}/backups
        retention:
          max_count: 10
          max_age_days: 30
        files:
          - name: config
            paths: [{{temp.Path}}]
            schedule: daily
        {{extra}}
        """;

    private static void AssertField(JsonElement embed, string name, string value)
    {
        var field = embed.GetProperty("fields").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        Assert.Equal(value, field.GetProperty("value").GetString());
    }

    private static int CountEmbedCharacters(JsonElement embeds)
    {
        return embeds.EnumerateArray().Sum(embed =>
            embed.GetProperty("title").GetString()!.Length
            + embed.GetProperty("footer").GetProperty("text").GetString()!.Length
            + embed.GetProperty("fields").EnumerateArray().Sum(field =>
                field.GetProperty("name").GetString()!.Length
                + field.GetProperty("value").GetString()!.Length));
    }

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responseFactory(Bodies.Count - 1);
        }
    }

    private sealed class StubChannel(
        string name,
        Func<NotificationEvent, Task<IReadOnlyList<string>>> send) : INotificationChannel
    {
        public string Name => name;
        public bool Called { get; private set; }

        public Task<IReadOnlyList<string>> SendAsync(
            NotificationEvent notification,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return send(notification);
        }
    }
}

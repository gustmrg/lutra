using Lutra.CLI.Commands;
using Lutra.CLI.Commands.Config;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;
using Lutra.Core.Notifications;
using Lutra.Core.Persistence;
using Lutra.Core.Processes;
using Lutra.Core.Restore;
using Lutra.Core.Sync;

namespace Lutra.CLI.Infrastructure;

internal static class ServiceFactory
{
    private static readonly HttpClient NotificationHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static BackupConfig LoadConfig(GlobalSettings settings)
    {
        var (configPath, envPath) = ConfigFileHelper.ResolvePaths(
            settings.ConfigPath,
            settings.EnvFilePath);

        YamlConfigLoader.LoadEnvFile(envPath);
        var loader = new YamlConfigLoader();
        return loader.Load(configPath);
    }

    public static BackupOrchestrator CreateOrchestrator(BackupConfig config)
    {
        var historyService = CreateHistoryService(config);
        return new BackupOrchestrator(new SystemProcessRunner(), historyService, config);
    }

    public static RestoreOrchestrator CreateRestoreOrchestrator(BackupConfig config)
    {
        var historyService = CreateHistoryService(config);
        return new RestoreOrchestrator(new SystemProcessRunner(), historyService, config);
    }

    public static IBackupHistoryService CreateHistoryService(BackupConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.StateDirectory)
            || string.IsNullOrWhiteSpace(config.ConfigPath))
        {
            throw new ConfigurationException(
                "The configuration must be loaded from a file before application state can be opened.");
        }

        try
        {
            var database = new LutraDatabase(
                config.StateDirectory,
                config.ConfigPath);
            return new SqliteBackupHistoryRepository(database);
        }
        catch (LutraDatabaseOwnershipException ex)
        {
            throw new ConfigurationException(
                $"{ex.Message} Use a distinct explicit state_directory for this configuration.",
                ex);
        }
    }

    public static BackupReconciliationService CreateReconciliationService(BackupConfig config)
    {
        return new BackupReconciliationService(config, CreateHistoryService(config));
    }

    public static OrphanCleanupService CreateOrphanCleanupService(BackupConfig config)
    {
        return new OrphanCleanupService(config, CreateHistoryService(config));
    }

    public static NotificationService? CreateNotificationService(BackupConfig config)
    {
        if (config.Notifications?.Discord is not { } discord)
            return null;

        var channel = new DiscordNotificationChannel(
            NotificationHttpClient,
            DiscordWebhookUrlResolver.Resolve(discord));
        return new NotificationService([channel]);
    }

    public static RsyncService CreateRsyncService(BackupConfig config)
    {
        return new RsyncService(config, CreateHistoryService(config));
    }

    public static DatabaseTarget ResolveTarget(BackupConfig config, string targetName)
    {
        var target = config.Databases.FirstOrDefault(
            t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            var available = string.Join(", ", config.Databases.Select(t => t.Name));
            throw new ConfigurationException(
                $"Target '{targetName}' not found. Available targets: {available}");
        }

        return target;
    }
}

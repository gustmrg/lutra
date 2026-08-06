using Lutra.CLI.Commands;
using Lutra.CLI.Commands.Config;
using Lutra.Core.Backup;
using Lutra.Core.Bundle;
using Lutra.Core.Configuration;
using Lutra.Core.Health;
using Lutra.Core.History;
using Lutra.Core.Inventory;
using Lutra.Core.Notifications;
using Lutra.Core.Persistence;
using Lutra.Core.Restore;
using Lutra.Core.Recovery;
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
        var processExecutor = new DockerProcessExecutor();
        IBackupProvider[] providers =
        [
            new PostgresBackupProvider(),
            new SqlServerBackupProvider(),
            new MongoBackupProvider(),
            new SqliteBackupProvider()
        ];
        return new BackupOrchestrator(providers, processExecutor, historyService, config);
    }

    public static RestoreOrchestrator CreateRestoreOrchestrator(BackupConfig config)
    {
        var historyService = CreateHistoryService(config);
        var processExecutor = new DockerProcessExecutor();
        IRestoreProvider[] providers =
        [
            new PostgresRestoreProvider(),
            new SqlServerRestoreProvider(),
            new MongoRestoreProvider(),
            new SqliteRestoreProvider()
        ];
        return new RestoreOrchestrator(providers, processExecutor, historyService, config);
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
                config.ConfigPath,
                config.BackupDirectory);
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

    public static AnomalyDetector CreateAnomalyDetector(BackupConfig config)
    {
        return new AnomalyDetector(config.Health ?? new HealthConfig());
    }

    public static BackupArtifactHealthChecker CreateArtifactHealthChecker(BackupConfig config)
    {
        return new BackupArtifactHealthChecker(config, CreateHistoryService(config));
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

    public static DisasterRecoveryBundleService CreateBundleService(BackupConfig config)
    {
        return new DisasterRecoveryBundleService(config, CreateHistoryService(config));
    }

    public static InventoryService CreateInventoryService(BackupConfig config)
    {
        return new InventoryService(config);
    }

    public static EnvironmentBackupService CreateEnvironmentBackupService(BackupConfig config)
    {
        return new EnvironmentBackupService(
            config,
            CreateHistoryService(config),
            CreateInventoryService(config));
    }

    public static IBackupTarget ResolveTarget(BackupConfig config, string targetName)
    {
        var target = config.AllTargets().FirstOrDefault(
            t => t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            var available = string.Join(", ", config.AllTargets().Select(t => t.Name));
            throw new ConfigurationException(
                $"Target '{targetName}' not found. Available targets: {available}");
        }

        return target;
    }

    public static DatabaseTarget ResolveDatabaseTarget(BackupConfig config, string targetName)
    {
        var target = ResolveTarget(config, targetName);

        if (target is not DatabaseTarget databaseTarget)
        {
            throw new ConfigurationException(
                $"Target '{targetName}' is not a database target; this command requires a database target.");
        }

        return databaseTarget;
    }
}

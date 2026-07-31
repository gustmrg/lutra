using Lutra.CLI.Commands;
using Lutra.CLI.Commands.Config;
using Lutra.Core.Backup;
using Lutra.Core.Bundle;
using Lutra.Core.Configuration;
using Lutra.Core.Health;
using Lutra.Core.History;
using Lutra.Core.Inventory;
using Lutra.Core.Notifications;
using Lutra.Core.Restore;
using Lutra.Core.Sync;

namespace Lutra.CLI.Infrastructure;

internal static class ServiceFactory
{
    public static BackupConfig LoadConfig(GlobalSettings settings)
    {
        var envPath = ConfigFileHelper.ResolveEnvPath(settings.EnvFilePath);
        var configPath = ConfigFileHelper.ResolveConfigPath(settings.ConfigPath);

        YamlConfigLoader.LoadEnvFile(envPath);
        var loader = new YamlConfigLoader();
        return loader.Load(configPath);
    }

    public static BackupOrchestrator CreateOrchestrator(BackupConfig config)
    {
        var historyService = new BackupHistoryService(config.BackupDirectory);
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
        var historyService = new BackupHistoryService(config.BackupDirectory);
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

    public static BackupHistoryService CreateHistoryService(BackupConfig config)
    {
        return new BackupHistoryService(config.BackupDirectory);
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
        return new BackupArtifactHealthChecker(config);
    }

    public static NotificationService? CreateNotificationService(BackupConfig config)
    {
        return config.Notifications is null ? null : new NotificationService(config.Notifications);
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

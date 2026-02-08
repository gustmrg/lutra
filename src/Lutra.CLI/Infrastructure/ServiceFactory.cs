using Lutra.CLI.Commands;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;

namespace Lutra.CLI.Infrastructure;

internal static class ServiceFactory
{
    public static BackupConfig LoadConfig(GlobalSettings settings)
    {
        YamlConfigLoader.LoadEnvFile(settings.EnvFilePath);
        var loader = new YamlConfigLoader();
        return loader.Load(settings.ConfigPath);
    }

    public static BackupOrchestrator CreateOrchestrator(BackupConfig config)
    {
        var historyService = new BackupHistoryService(config.BackupDirectory);
        var processExecutor = new DockerProcessExecutor();
        IBackupProvider[] providers =
        [
            new PostgresBackupProvider(),
            new SqlServerBackupProvider(),
            new MongoBackupProvider()
        ];
        return new BackupOrchestrator(providers, processExecutor, historyService, config);
    }

    public static BackupHistoryService CreateHistoryService(BackupConfig config)
    {
        return new BackupHistoryService(config.BackupDirectory);
    }

    public static DatabaseTarget ResolveTarget(BackupConfig config, string targetName)
    {
        var target = config.Databases.FirstOrDefault(
            d => d.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            var available = string.Join(", ", config.Databases.Select(d => d.Name));
            throw new ConfigurationException(
                $"Target '{targetName}' not found. Available targets: {available}");
        }

        return target;
    }
}

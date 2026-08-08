using Lutra.CLI.Commands.Backup;
using Lutra.CLI.Commands.Bundle;
using Lutra.CLI.Commands.Cleanup;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Commands.Recovery;
using Lutra.CLI.Commands.Health;
using Lutra.CLI.Commands.History;
using Lutra.CLI.Commands.Inventory;
using Lutra.CLI.Commands.Restore;
using Lutra.CLI.Commands.Schedule;
using Lutra.CLI.Commands.Sync;
using Lutra.CLI.Commands.Uninstall;
using Lutra.CLI.Commands.Verify;
using System.Reflection;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("lutra");
    config.SetApplicationVersion(
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    config.AddBranch<CommandSettings>("backup", backup =>
    {
        backup.SetDescription("Run and manage backups for databases, files, and Docker volumes.");

        backup.AddCommand<BackupRunCommand>("run")
            .WithDescription("Run backups for all configured targets or one target.");

        backup.AddCommand<BackupListCommand>("list")
            .WithDescription("List all configured backup targets.");

        backup.AddCommand<BackupVerifyFileCommand>("verify-file")
            .WithDescription("Verify one backup artifact against its checksum and manifest sidecars.");

        backup.AddCommand<BackupReconcileCommand>("reconcile")
            .WithDescription("Find inconsistencies among backup artifacts, sidecars, and history.");
    });

    config.AddCommand<HistoryCommand>("history")
        .WithDescription("Show backup, verification, and sync history.");

    config.AddCommand<RestoreCommand>("restore")
        .WithDescription("Restore a backup into its configured target (destructive).");

    config.AddCommand<VerifyCommand>("verify")
        .WithDescription("Verify a backup without changing its configured target.");

    config.AddCommand<CleanupCommand>("cleanup")
        .WithDescription("Apply retention cleanup and optionally remove orphan artifacts or history.");

    config.AddCommand<HealthCommand>("health")
        .WithDescription("Analyze backup age, integrity, and operational anomalies.");

    config.AddCommand<InventoryCommand>("inventory")
        .WithDescription("Capture a best-effort server inventory snapshot when enabled.");

    config.AddCommand<SyncCommand>("sync")
        .WithDescription("Push configured backups to an SSH/rsync destination.");

    config.AddCommand<BundleCommand>("bundle")
        .WithDescription("Create a disaster recovery bundle from latest artifacts and restore instructions.");

    config.AddBranch<CommandSettings>("environment", environment =>
    {
        environment.SetDescription("Create and restore structured VPS environment recovery sets.");
        environment.AddCommand<EnvironmentBackupCommand>("backup")
            .WithDescription("Create a plaintext environment recovery set with secrets excluded.");
        environment.AddCommand<EnvironmentInspectCommand>("inspect")
            .WithDescription("Verify and summarize an environment recovery set.");
        environment.AddCommand<EnvironmentRestoreCommand>("restore")
            .WithDescription("Preflight or apply a guarded environment recovery plan.");
    });

    config.AddBranch<CommandSettings>("config", cfg =>
    {
        cfg.SetDescription("Configuration management.");

        cfg.AddCommand<ConfigInitCommand>("init")
            .WithDescription("Create configuration, environment, and backup directories.");

        cfg.AddCommand<ConfigValidateCommand>("validate")
            .WithDescription("Validate configuration, target paths, and the backup directory.");

        cfg.AddCommand<ConfigResetCommand>("reset")
            .WithDescription("Reset configuration and environment files to template defaults.");

        cfg.AddCommand<ConfigGenerateCommand>("generate")
            .WithDescription("Generate database targets from a Docker Compose file.");
    });

    config.AddCommand<UninstallCommand>("uninstall")
        .WithDescription("Remove Lutra's binary, configuration, timers, and optionally backup data.");

    config.AddBranch<CommandSettings>("schedule", schedule =>
    {
        schedule.SetDescription("Manage Lutra systemd timers for backups, restore drills, and inventory.");

        schedule.AddCommand<ScheduleInstallCommand>("install")
            .WithDescription("Install systemd timer units for backups, restore drills, and inventory.");

        schedule.AddCommand<ScheduleRemoveCommand>("remove")
            .WithDescription("Remove installed Lutra systemd timer units.");

        schedule.AddCommand<ScheduleListCommand>("list")
            .WithDescription("List installed Lutra systemd timer units and their status.");
    });
});

return app.Run(args);

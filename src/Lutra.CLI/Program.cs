using Lutra.CLI.Commands.Backup;
using Lutra.CLI.Commands.Cleanup;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Commands.Health;
using Lutra.CLI.Commands.History;
using Lutra.CLI.Commands.Inventory;
using Lutra.CLI.Commands.Restore;
using Lutra.CLI.Commands.Schedule;
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
        backup.SetDescription("Run and manage database backups.");

        backup.AddCommand<BackupRunCommand>("run")
            .WithDescription("Run backups for all or a specific database target.");

        backup.AddCommand<BackupListCommand>("list")
            .WithDescription("List configured database targets.");

        backup.AddCommand<BackupVerifyFileCommand>("verify-file")
            .WithDescription("Verify a backup file against its checksum and manifest sidecars.");

        backup.AddCommand<BackupReconcileCommand>("reconcile")
            .WithDescription("Find inconsistencies between backup files, sidecars, and history.");
    });

    config.AddCommand<HistoryCommand>("history")
        .WithDescription("Show backup history.");

    config.AddCommand<RestoreCommand>("restore")
        .WithDescription("Restore a backup into its database (destructive).");

    config.AddCommand<VerifyCommand>("verify")
        .WithDescription("Verify a backup by test-restoring it into a temporary database.");

    config.AddCommand<CleanupCommand>("cleanup")
        .WithDescription("Run retention cleanup to remove old backups.");

    config.AddCommand<HealthCommand>("health")
        .WithDescription("Analyze backup health and detect anomalies.");

    config.AddCommand<InventoryCommand>("inventory")
        .WithDescription("Capture a best-effort server inventory snapshot.");

    config.AddBranch<CommandSettings>("config", cfg =>
    {
        cfg.SetDescription("Configuration management.");

        cfg.AddCommand<ConfigInitCommand>("init")
            .WithDescription("Initialize configuration files and directories.");

        cfg.AddCommand<ConfigValidateCommand>("validate")
            .WithDescription("Validate the configuration file.");

        cfg.AddCommand<ConfigResetCommand>("reset")
            .WithDescription("Reset configuration files to template defaults.");

        cfg.AddCommand<ConfigGenerateCommand>("generate")
            .WithDescription("Generate config from a docker-compose file.");
    });

    config.AddCommand<UninstallCommand>("uninstall")
        .WithDescription("Remove all Lutra artifacts (config, timers, binary).");

    config.AddBranch<CommandSettings>("schedule", schedule =>
    {
        schedule.SetDescription("Manage systemd timers for scheduled backups.");

        schedule.AddCommand<ScheduleInstallCommand>("install")
            .WithDescription("Install systemd timer units for scheduled backups.");

        schedule.AddCommand<ScheduleRemoveCommand>("remove")
            .WithDescription("Remove systemd timer units for scheduled backups.");

        schedule.AddCommand<ScheduleListCommand>("list")
            .WithDescription("List installed Lutra systemd timer units.");
    });
});

return app.Run(args);

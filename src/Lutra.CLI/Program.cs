using Lutra.CLI.Commands;
using Lutra.CLI.Commands.Backup;
using Lutra.CLI.Commands.Cleanup;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Commands.History;
using Lutra.CLI.Commands.Schedule;
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
    });

    config.AddCommand<HistoryCommand>("history")
        .WithDescription("Show backup history.");

    config.AddCommand<CleanupCommand>("cleanup")
        .WithDescription("Run retention cleanup to remove old backups.");

    config.AddBranch<CommandSettings>("config", cfg =>
    {
        cfg.SetDescription("Configuration management.");

        cfg.AddCommand<ConfigInitCommand>("init")
            .WithDescription("Initialize configuration files and directories.");

        cfg.AddCommand<ConfigValidateCommand>("validate")
            .WithDescription("Validate the configuration file.");
    });

    config.AddBranch<CommandSettings>("schedule", schedule =>
    {
        schedule.SetDescription("Manage systemd timers for scheduled backups.");

        schedule.AddCommand<ScheduleInstallCommand>("install")
            .WithDescription("Install systemd timer units for scheduled backups.");
    });
});

return app.Run(args);

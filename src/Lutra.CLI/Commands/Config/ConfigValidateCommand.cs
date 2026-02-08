using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigValidateCommand : AsyncCommand<GlobalSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);

            AnsiConsole.MarkupLine("[green]Configuration is valid.[/]");
            AnsiConsole.MarkupLine($"  Backup directory: [blue]{config.BackupDirectory.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine($"  Retention: max_count={config.Retention.MaxCount}, max_age_days={config.Retention.MaxAgeDays}");
            AnsiConsole.MarkupLine($"  Database targets: [blue]{config.Databases.Count}[/]");

            foreach (var db in config.Databases)
            {
                AnsiConsole.MarkupLine($"    - {db.Name.EscapeMarkup()} ({db.Type}, container: {db.Container.EscapeMarkup()})");
            }

            return Task.FromResult(0);
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return Task.FromResult(1);
        }
    }
}

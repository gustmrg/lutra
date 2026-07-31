using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupListCommand : AsyncCommand<GlobalSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);

            var table = new Table();
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn("Container");
            table.AddColumn("Database");
            table.AddColumn("Schedule");
            table.AddColumn("Compression");

            foreach (var db in config.Databases)
            {
                table.AddRow(
                    db.Name.EscapeMarkup(),
                    db.Type.ToString(),
                    db.Container.EscapeMarkup(),
                    db.Database.EscapeMarkup(),
                    db.Schedule.EscapeMarkup(),
                    db.Compression.ToString());
            }

            AnsiConsole.Write(table);

            if (config.Files.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[bold]File targets[/]");

                var filesTable = new Table();
                filesTable.AddColumn("Name");
                filesTable.AddColumn("Paths");
                filesTable.AddColumn("Excludes");
                filesTable.AddColumn("Schedule");
                filesTable.AddColumn("Compression");

                foreach (var ft in config.Files)
                {
                    filesTable.AddRow(
                        ft.Name.EscapeMarkup(),
                        string.Join("\n", ft.Paths).EscapeMarkup(),
                        ft.Exclude is { Count: > 0 } ? string.Join(", ", ft.Exclude).EscapeMarkup() : "-",
                        ft.Schedule.EscapeMarkup(),
                        ft.Compression.ToString());
                }

                AnsiConsole.Write(filesTable);
            }

            if (config.Volumes.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[bold]Docker volume targets[/]");
                var volumeTable = new Table();
                volumeTable.AddColumn("Name");
                volumeTable.AddColumn("Volume");
                volumeTable.AddColumn("Schedule");
                volumeTable.AddColumn("Compression");
                foreach (var volume in config.Volumes)
                    volumeTable.AddRow(volume.Name.EscapeMarkup(), volume.Volume.EscapeMarkup(), volume.Schedule.EscapeMarkup(), volume.Compression.ToString());
                AnsiConsole.Write(volumeTable);
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

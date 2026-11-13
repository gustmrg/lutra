using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupListCommand : LutraCommand<GlobalSettings>
{
    protected override Task<int> RunAsync(GlobalSettings settings)
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

        return Task.FromResult(0);
    }
}

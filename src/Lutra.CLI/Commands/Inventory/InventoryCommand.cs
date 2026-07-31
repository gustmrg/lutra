using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Inventory;

public sealed class InventoryCommand : AsyncCommand<GlobalSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            if (config.Inventory is not { Enabled: true })
            {
                AnsiConsole.MarkupLine("[yellow]Inventory snapshots are not enabled in the configuration.[/]");
                return 1;
            }

            var result = await AnsiConsole.Status()
                .StartAsync("Capturing server inventory...", _ =>
                    ServiceFactory.CreateInventoryService(config).CaptureAsync());

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Inventory snapshot failed:[/] {result.ErrorMessage?.EscapeMarkup() ?? "Unknown error"}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Inventory snapshot created:[/] {result.FilePath!.EscapeMarkup()}");
            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }
}

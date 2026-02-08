using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Cleanup;

public sealed class CleanupCommand : AsyncCommand<TargetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TargetSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var orchestrator = ServiceFactory.CreateOrchestrator(config);

            var totalDeleted = 0;

            if (settings.Target is not null)
            {
                var target = ServiceFactory.ResolveTarget(config, settings.Target);
                var deleted = await orchestrator.CleanupAsync(target);
                totalDeleted += deleted;
                AnsiConsole.MarkupLine($"  {target.Name.EscapeMarkup()}: removed [blue]{deleted}[/] backup(s)");
            }
            else
            {
                foreach (var target in config.Databases)
                {
                    var deleted = await orchestrator.CleanupAsync(target);
                    totalDeleted += deleted;
                    AnsiConsole.MarkupLine($"  {target.Name.EscapeMarkup()}: removed [blue]{deleted}[/] backup(s)");
                }
            }

            AnsiConsole.MarkupLine($"\n[green]Cleanup complete.[/] Removed {totalDeleted} total backup(s).");
            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }
}

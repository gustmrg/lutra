using Lutra.CLI.Infrastructure;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Cleanup;

public sealed class CleanupCommand : AsyncCommand<CleanupSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CleanupSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var orchestrator = ServiceFactory.CreateOrchestrator(config);

            var totalDeleted = 0;

            if (settings.Target is not null)
            {
                var target = ServiceFactory.ResolveTarget(config, settings.Target);
                var deleted = await RunCleanupAsync(orchestrator, target, settings.DryRun);
                totalDeleted += deleted;
            }
            else
            {
                foreach (var target in config.AllTargets())
                {
                    var deleted = await RunCleanupAsync(orchestrator, target, settings.DryRun);
                    totalDeleted += deleted;
                }
            }

            if (settings.DryRun)
                AnsiConsole.MarkupLine($"\n[yellow]Dry run complete.[/] {totalDeleted} backup(s) would be removed.");
            else
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

    private static async Task<int> RunCleanupAsync(
        BackupOrchestrator orchestrator,
        IBackupTarget target,
        bool dryRun)
    {
        if (!dryRun)
        {
            var deleted = await orchestrator.CleanupAsync(target);
            AnsiConsole.MarkupLine($"  {target.Name.EscapeMarkup()}: removed [blue]{deleted}[/] backup(s)");
            return deleted;
        }

        var candidates = await orchestrator.PreviewCleanupAsync(target);
        AnsiConsole.MarkupLine($"  {target.Name.EscapeMarkup()}: would remove [blue]{candidates.Count}[/] backup(s)");

        foreach (var candidate in candidates)
        {
            AnsiConsole.MarkupLine($"    - {candidate.Record.FileName.EscapeMarkup()}");
            foreach (var path in candidate.PathsToDelete)
                AnsiConsole.MarkupLine($"      [grey]{path.EscapeMarkup()}[/]");
        }

        return candidates.Count;
    }
}

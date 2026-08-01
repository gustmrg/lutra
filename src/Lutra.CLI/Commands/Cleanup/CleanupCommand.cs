using Lutra.CLI.Infrastructure;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.History;
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

            var targets = settings.Target is not null
                ? new List<IBackupTarget> { ServiceFactory.ResolveTarget(config, settings.Target) }
                : config.AllTargets().ToList();
            var totalDeleted = 0;

            foreach (var target in targets)
                totalDeleted += await RunCleanupAsync(orchestrator, target, settings.DryRun);

            if (settings.OrphanFiles && !settings.DryRun && !ConfirmUntrackedDeletion(settings.Force))
                return 1;

            if (settings.OrphanSidecars || settings.OrphanFiles)
            {
                var orphanService = ServiceFactory.CreateOrphanCleanupService(config);
                foreach (var target in targets)
                {
                    var candidates = await orphanService.FindAsync(target, settings.OrphanFiles);
                    if (!settings.OrphanSidecars)
                        candidates = candidates.Where(candidate => candidate.Kind == OrphanKind.BackupWithoutHistory).ToList();

                    foreach (var candidate in candidates)
                    {
                        AnsiConsole.MarkupLine($"  {target.Name.EscapeMarkup()}: {(settings.DryRun ? "would remove" : "removing")} [yellow]{candidate.Kind}[/]");
                        foreach (var path in candidate.Paths.Where(File.Exists))
                            AnsiConsole.MarkupLine($"    [grey]{path.EscapeMarkup()}[/]");
                    }
                    if (!settings.DryRun)
                        OrphanCleanupService.Delete(candidates);
                }
            }

            if (settings.PruneHistory)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-config.Retention.MaxAgeDays);
                var history = ServiceFactory.CreateHistoryService(config);
                if (settings.DryRun)
                {
                    var records = await history.GetAllRecordsAsync();
                    var count = records.Count(record => record.StartedAt < cutoff
                        && record.Status.IsTerminal()
                        && (record.OperationType != Lutra.Core.History.HistoryOperationType.Backup
                            || record.Status != Lutra.Core.History.HistoryOperationStatus.Succeeded));
                    AnsiConsole.MarkupLine($"  History: would prune [blue]{count}[/] operational record(s)");
                }
                else
                {
                    var count = await history.PruneOperationalRecordsAsync(cutoff);
                    AnsiConsole.MarkupLine($"  History: pruned [blue]{count}[/] operational record(s)");
                }
            }

            if (settings.DryRun)
                AnsiConsole.MarkupLine($"\n[yellow]Dry run complete.[/] {totalDeleted} retained backup(s) would be removed.");
            else
                AnsiConsole.MarkupLine($"\n[green]Cleanup complete.[/] Removed {totalDeleted} retained backup(s).");

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

    private static bool ConfirmUntrackedDeletion(bool force)
    {
        if (force)
            return true;
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]--orphan-files requires confirmation.[/] Use --force for non-interactive cleanup.");
            return false;
        }
        return AnsiConsole.Prompt(new ConfirmationPrompt(
            "Delete backup files that have no successful history entry?") { DefaultValue = false });
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

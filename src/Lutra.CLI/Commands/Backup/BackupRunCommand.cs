using Lutra.CLI.Infrastructure;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Notifications;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupRunCommand : AsyncCommand<TargetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TargetSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var orchestrator = ServiceFactory.CreateOrchestrator(config);

            IReadOnlyList<BackupResult> results;

            if (settings.Target is not null)
            {
                var target = ServiceFactory.ResolveTarget(config, settings.Target);
                var result = await AnsiConsole.Status()
                    .StartAsync($"Backing up {target.Name}...", _ => orchestrator.BackupAsync(target, AppCancellation.Token));

                results = [result];
            }
            else
            {
                results = await AnsiConsole.Status()
                    .StartAsync("Running backups for all targets...", async _ =>
                        await orchestrator.BackupAllAsync(AppCancellation.Token));
            }

            PrintResults(results);

            var success = results.All(result => result.Success);
            await NotificationConsole.SendAsync(
                config,
                success ? "backup_success" : "backup_failure",
                success,
                success
                    ? $"{results.Count} backup(s) completed successfully."
                    : $"{results.Count(result => !result.Success)} of {results.Count} backup(s) failed.",
                settings.Target,
                BackupNotificationMapper.Map(results, config));

            var syncSucceeded = true;
            if (results.All(result => result.Success) && config.Sync is { PostBackup: true })
            {
                var sync = await AnsiConsole.Status()
                    .StartAsync("Syncing completed backups offsite...", _ =>
                        ServiceFactory.CreateRsyncService(config).SyncAsync(settings.Target, dryRun: false, delete: false, AppCancellation.Token));
                syncSucceeded = sync.Success;
                if (sync.Success)
                    AnsiConsole.MarkupLine("[green]Post-backup offsite sync completed.[/]");
                else
                    AnsiConsole.MarkupLine($"[red]Post-backup offsite sync failed:[/] {sync.ErrorMessage?.EscapeMarkup() ?? "Unknown error"}");

                await NotificationConsole.SendAsync(
                    config,
                    sync.Success ? "sync_success" : "sync_failure",
                    sync.Success,
                    sync.Success ? "Post-backup offsite sync succeeded." : "Post-backup offsite sync failed.",
                    settings.Target);
            }

            return results.All(r => r.Success) && syncSucceeded ? 0 : 1;
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

    private static void PrintResults(IReadOnlyList<BackupResult> results)
    {
        var table = new Table();
        table.AddColumn("Target");
        table.AddColumn("Status");
        table.AddColumn("Duration");
        table.AddColumn("Size");
        table.AddColumn("SHA-256");
        table.AddColumn("File");

        foreach (var result in results)
        {
            if (result.Success)
            {
                var size = result.FileSizeBytes.HasValue ? FormatBytes(result.FileSizeBytes.Value) : "-";
                table.AddRow(
                    result.TargetName.EscapeMarkup(),
                    "[green]OK[/]",
                    result.Duration.TotalSeconds.ToString("0.0") + "s",
                    size,
                    result.Sha256 is null ? "-" : result.Sha256[..12],
                    result.FilePath?.EscapeMarkup() ?? "-");
            }
            else
            {
                table.AddRow(
                    result.TargetName.EscapeMarkup(),
                    "[red]FAILED[/]",
                    result.Duration.TotalSeconds.ToString("0.0") + "s",
                    "-",
                    "-",
                    result.ErrorMessage?.EscapeMarkup() ?? "Unknown error");
            }
        }

        AnsiConsole.Write(table);

        foreach (var result in results.Where(result => result.Warning is not null))
            AnsiConsole.MarkupLine($"[yellow]{result.TargetName.EscapeMarkup()}:[/] {result.Warning!.EscapeMarkup()}");

        var successCount = results.Count(r => r.Success);
        var failCount = results.Count - successCount;

        if (failCount == 0)
            AnsiConsole.MarkupLine($"\n[green]{successCount} backup(s) completed successfully.[/]");
        else
            AnsiConsole.MarkupLine($"\n[yellow]{successCount} succeeded, {failCount} failed.[/]");
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {suffixes[order]}";
    }
}

using Lutra.CLI.Infrastructure;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
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
                    .StartAsync($"Backing up {target.Name}...", async _ => target switch
                    {
                        DatabaseTarget db => await orchestrator.BackupAsync(db),
                        FileTarget files => await orchestrator.BackupFilesAsync(files),
                        VolumeTarget volume => await orchestrator.BackupVolumeAsync(volume),
                        _ => throw new ConfigurationException($"Unknown target type for '{target.Name}'.")
                    });

                if (result.Success && target is DatabaseTarget
                    {
                        Type: DatabaseType.PostgreSql,
                        PostgresWalArchivePath: not null
                    } postgres)
                {
                    var walResult = await AnsiConsole.Status()
                        .StartAsync($"Archiving WAL for {target.Name}...", _ =>
                            orchestrator.BackupPostgresWalAsync(postgres));
                    results = [result, walResult];
                }
                else
                {
                    results = [result];
                }
            }
            else
            {
                results = await AnsiConsole.Status()
                    .StartAsync("Running backups for all targets...", async _ =>
                        await orchestrator.BackupAllAsync());
            }

            PrintResults(results);

            var notification = ServiceFactory.CreateNotificationService(config);
            if (notification is not null)
            {
                var success = results.All(result => result.Success);
                await notification.NotifyAsync(
                    success ? "backup_success" : "backup_failure",
                    success,
                    success
                        ? $"{results.Count} backup(s) completed successfully."
                        : $"{results.Count(result => !result.Success)} of {results.Count} backup(s) failed.",
                    settings.Target);
            }

            // A full run also captures the optional host inventory. It is best-effort:
            // an inventory failure is visible but never changes the backup exit status.
            if (settings.Target is null && config.Inventory is { Enabled: true })
            {
                var inventory = await AnsiConsole.Status()
                    .StartAsync("Capturing server inventory...", _ =>
                        ServiceFactory.CreateInventoryService(config).CaptureAsync());
                if (inventory.Success)
                    AnsiConsole.MarkupLine($"[green]Inventory snapshot:[/] {inventory.FilePath!.EscapeMarkup()}");
                else
                    AnsiConsole.MarkupLine($"[yellow]Inventory snapshot failed (backups are unaffected):[/] {inventory.ErrorMessage?.EscapeMarkup() ?? "Unknown error"}");
            }

            var syncSucceeded = true;
            if (results.All(result => result.Success) && config.Sync is { PostBackup: true })
            {
                var sync = await AnsiConsole.Status()
                    .StartAsync("Syncing completed backups offsite...", _ =>
                        ServiceFactory.CreateRsyncService(config).SyncAsync(settings.Target, dryRun: false, delete: false));
                syncSucceeded = sync.Success;
                if (sync.Success)
                    AnsiConsole.MarkupLine("[green]Post-backup offsite sync completed.[/]");
                else
                    AnsiConsole.MarkupLine($"[red]Post-backup offsite sync failed:[/] {sync.ErrorMessage?.EscapeMarkup() ?? "Unknown error"}");

                if (notification is not null)
                {
                    await notification.NotifyAsync(
                        sync.Success ? "sync_success" : "sync_failure",
                        sync.Success,
                        sync.Success ? "Post-backup offsite sync succeeded." : "Post-backup offsite sync failed.",
                        settings.Target);
                }
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

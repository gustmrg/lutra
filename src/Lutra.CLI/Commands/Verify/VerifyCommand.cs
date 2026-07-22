using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Verify;

public sealed class VerifyCommand : AsyncCommand<VerifySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, VerifySettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);

            IBackupTarget? target;
            if (settings.Target is not null)
            {
                target = ServiceFactory.ResolveTarget(config, settings.Target);
            }
            else
            {
                if (!EnsureInteractive())
                    return 1;

                target = BackupFileSelection.PromptForTarget(config);
                if (target is null)
                {
                    AnsiConsole.MarkupLine("[yellow]No targets are configured.[/]");
                    return 1;
                }
            }

            var historyService = ServiceFactory.CreateHistoryService(config);

            string backupFile;
            if (settings.File is not null)
            {
                backupFile = BackupFileSelection.ResolveBackupFilePath(config, target, settings.File);
            }
            else
            {
                var latest = await BackupFileSelection.FindLatestBackupAsync(config, historyService, target);
                if (latest is null)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]No successful backups found for target '{target.Name.EscapeMarkup()}'.[/]");
                    return 1;
                }
                backupFile = BackupFileSelection.GetBackupPath(config, target, latest);
            }

            if (!File.Exists(backupFile))
            {
                AnsiConsole.MarkupLine($"[red]Backup file not found:[/] {backupFile.EscapeMarkup()}");
                return 1;
            }

            AnsiConsole.MarkupLine(
                $"Verifying [bold]{Path.GetFileName(backupFile).EscapeMarkup()}[/]...");

            var orchestrator = ServiceFactory.CreateRestoreOrchestrator(config);
            var result = await AnsiConsole.Status()
                .StartAsync("Verifying backup...", async _ => target switch
                {
                    DatabaseTarget db => await orchestrator.TestRestoreAsync(db, backupFile),
                    FileTarget files => await orchestrator.VerifyFilesAsync(files, backupFile),
                    _ => throw new ConfigurationException($"Unknown target type for '{target.Name}'.")
                });

            if (result.Success)
            {
                AnsiConsole.MarkupLine("[green]Verification passed[/]");
                AnsiConsole.MarkupLine($"  Checksum: [green]valid[/]");
                if (target is DatabaseTarget)
                    AnsiConsole.MarkupLine($"  Test-restore: [green]succeeded[/] (temporary database dropped)");
                if (result.ValidationDetails is not null)
                    AnsiConsole.MarkupLine($"  {result.ValidationDetails.EscapeMarkup()}");
                AnsiConsole.MarkupLine($"  Duration: {result.Duration.TotalSeconds:0.0}s");
                return 0;
            }

            AnsiConsole.MarkupLine("[red]Verification failed[/]");
            if (result.ChecksumValid)
                AnsiConsole.MarkupLine("  Checksum: [green]valid[/]");
            AnsiConsole.MarkupLine($"  {result.ErrorMessage?.EscapeMarkup()}");
            return 1;
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

    private static bool EnsureInteractive()
    {
        if (AnsiConsole.Profile.Capabilities.Interactive)
            return true;

        AnsiConsole.MarkupLine(
            "[red]This command requires an interactive terminal.[/] " +
            "Use --target and --file for non-interactive execution.");
        return false;
    }
}

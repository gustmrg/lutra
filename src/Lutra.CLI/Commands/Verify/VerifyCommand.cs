using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Verify;

public sealed class VerifyCommand : LutraCommand<VerifySettings>
{
    protected override async Task<int> RunAsync(VerifySettings settings)
    {
        var config = ServiceFactory.LoadConfig(settings);

        DatabaseTarget? target;
        if (settings.Target is not null)
        {
            target = ServiceFactory.ResolveTarget(config, settings.Target);
        }
        else
        {
            if (!EnsureInteractive("Use --target and --file for non-interactive execution."))
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
            .StartAsync("Verifying backup...", _ => orchestrator.TestRestoreAsync(target, backupFile, AppCancellation.Token));

        await NotificationConsole.SendAsync(
            config,
            result.Success ? "verify_success" : "verify_failure",
            result.Success,
            result.Success ? "Backup verification succeeded." : "Backup verification failed.",
            target.Name);

        if (result.Success)
        {
            AnsiConsole.MarkupLine("[green]Verification passed[/]");
            AnsiConsole.MarkupLine($"  Checksum: [green]valid[/]");
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
}

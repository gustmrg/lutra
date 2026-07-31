using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Restore;

public sealed class RestoreCommand : AsyncCommand<RestoreSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RestoreSettings settings)
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
                if (!EnsureInteractive())
                    return 1;

                var record = await BackupFileSelection.PromptForBackupAsync(historyService, target);
                if (record is null)
                {
                    AnsiConsole.MarkupLine($"[yellow]No successful backups found for target '{target.Name.EscapeMarkup()}'.[/]");
                    return 1;
                }
                backupFile = BackupFileSelection.GetBackupPath(config, target, record);
            }

            if (!File.Exists(backupFile))
            {
                AnsiConsole.MarkupLine($"[red]Backup file not found:[/] {backupFile.EscapeMarkup()}");
                return 1;
            }

            var orchestrator = ServiceFactory.CreateRestoreOrchestrator(config);

            return target switch
            {
                DatabaseTarget databaseTarget => await RestoreDatabaseAsync(config, orchestrator, databaseTarget, backupFile, settings),
                FileTarget fileTarget => await RestoreFilesAsync(config, orchestrator, fileTarget, backupFile, settings),
                _ => 1
            };
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

    private static async Task<int> RestoreDatabaseAsync(
        BackupConfig config,
        Core.Restore.RestoreOrchestrator orchestrator,
        DatabaseTarget target,
        string backupFile,
        RestoreSettings settings)
    {
        AnsiConsole.Write(new Panel(
                $"Database:  [bold]{target.Database.EscapeMarkup()}[/]\n" +
                $"Container: [bold]{target.Container.EscapeMarkup()}[/]\n" +
                $"Backup:    [bold]{Path.GetFileName(backupFile).EscapeMarkup()}[/]")
            .Header("[red] DESTRUCTIVE OPERATION [/]")
            .Border(BoxBorder.Heavy)
            .BorderStyle(Color.Red));
        AnsiConsole.MarkupLine(
            "[red]This will overwrite the current contents of the database with the backup.[/]");

        if (!Confirm(settings.Force))
            return 1;

        var result = await AnsiConsole.Status()
            .StartAsync($"Restoring {target.Name}...", async _ =>
                await orchestrator.RestoreAsync(target, backupFile));

        await NotifyRestoreAsync(config, target.Name, result.Success);

        if (result.Success)
        {
            AnsiConsole.MarkupLine(
                $"[green]Restore completed[/] in {result.Duration.TotalSeconds:0.0}s. " +
                $"Database '{result.DestinationDatabase?.EscapeMarkup()}' was replaced with the backup contents.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]Restore failed:[/] {result.ErrorMessage?.EscapeMarkup()}");
        return 1;
    }

    private static async Task<int> RestoreFilesAsync(
        BackupConfig config,
        Core.Restore.RestoreOrchestrator orchestrator,
        FileTarget target,
        string backupFile,
        RestoreSettings settings)
    {
        var destination = settings.Destination ?? "/";

        AnsiConsole.Write(new Panel(
                $"Archive:     [bold]{Path.GetFileName(backupFile).EscapeMarkup()}[/]\n" +
                $"Destination: [bold]{destination.EscapeMarkup()}[/]")
            .Header("[red] DESTRUCTIVE OPERATION [/]")
            .Border(BoxBorder.Heavy)
            .BorderStyle(Color.Red));
        AnsiConsole.MarkupLine(
            "[red]Files in the archive will overwrite files with the same paths at the destination.[/]");

        if (!Confirm(settings.Force))
            return 1;

        var result = await AnsiConsole.Status()
            .StartAsync($"Extracting {target.Name}...", async _ =>
                await orchestrator.RestoreFilesAsync(target, backupFile, destination));

        await NotifyRestoreAsync(config, target.Name, result.Success);

        if (result.Success)
        {
            AnsiConsole.MarkupLine(
                $"[green]Restore completed[/] in {result.Duration.TotalSeconds:0.0}s. " +
                $"Archive extracted to '{result.DestinationDatabase?.EscapeMarkup()}'.");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red]Restore failed:[/] {result.ErrorMessage?.EscapeMarkup()}");
        return 1;
    }

    private static async Task NotifyRestoreAsync(BackupConfig config, string targetName, bool success)
    {
        var notification = ServiceFactory.CreateNotificationService(config);
        if (notification is not null)
        {
            await notification.NotifyAsync(
                success ? "restore_success" : "restore_failure",
                success,
                success ? "Backup restore succeeded." : "Backup restore failed.",
                targetName);
        }
    }

    private static bool Confirm(bool force)
    {
        if (force)
            return true;

        if (!EnsureInteractive())
            return false;

        var confirmed = AnsiConsole.Prompt(
            new ConfirmationPrompt("Proceed with restore?") { DefaultValue = false });
        if (!confirmed)
            AnsiConsole.MarkupLine("[yellow]Restore cancelled.[/]");

        return confirmed;
    }

    private static bool EnsureInteractive()
    {
        if (AnsiConsole.Profile.Capabilities.Interactive)
            return true;

        AnsiConsole.MarkupLine(
            "[red]This command requires an interactive terminal.[/] " +
            "Use --target, --file, and --force for non-interactive execution.");
        return false;
    }
}

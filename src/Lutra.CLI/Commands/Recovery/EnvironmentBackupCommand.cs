using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Lutra.Core.Recovery;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Recovery;

public sealed class EnvironmentBackupCommand : AsyncCommand<GlobalSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            if (config.Environment is not { Enabled: true })
            {
                AnsiConsole.MarkupLine("[yellow]Environment recovery is not enabled.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] environment recovery sets are plaintext and excluded from built-in sync.");
            var result = await AnsiConsole.Status().StartAsync(
                "Creating environment recovery set...",
                _ => ServiceFactory.CreateEnvironmentBackupService(config).BackupAsync());

            await NotificationConsole.SendAsync(
                config,
                result.Success ? "environment_backup_success" : "environment_backup_failure",
                result.Success,
                result.Success
                    ? "Environment recovery set created successfully."
                    : "Environment recovery backup failed.",
                EnvironmentBackupService.HistoryTargetName);

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Environment backup failed:[/] {result.ErrorMessage!.EscapeMarkup()}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Recovery set created:[/] {result.FilePath!.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"  Size: {result.FileSizeBytes:N0} bytes");
            AnsiConsole.MarkupLine($"  SHA-256: {result.Sha256![..12]}");
            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]Environment backup failed during initialization.[/]");
            return 1;
        }
    }
}

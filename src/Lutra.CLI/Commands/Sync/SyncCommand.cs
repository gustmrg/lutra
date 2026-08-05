using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Sync;

public sealed class SyncCommand : AsyncCommand<SyncSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SyncSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            if (config.Sync is null)
                throw new ConfigurationException("An rsync 'sync' section is not configured.");
            if (settings.Target is not null)
                ServiceFactory.ResolveTarget(config, settings.Target);

            var service = ServiceFactory.CreateRsyncService(config);
            if (settings.ValidateOnly)
            {
                var validation = await AnsiConsole.Status()
                    .StartAsync("Validating offsite sync...", _ => service.ValidateAsync());
                AnsiConsole.MarkupLine(validation.Success
                    ? $"[green]Sync validation passed:[/] {validation.Message.EscapeMarkup()}"
                    : $"[red]Sync validation failed:[/] {validation.Message.EscapeMarkup()}");
                return validation.Success ? 0 : 1;
            }

            var result = await AnsiConsole.Status()
                .StartAsync(settings.DryRun ? "Previewing offsite sync..." : "Syncing backups offsite...", _ =>
                    service.SyncAsync(settings.Target, settings.DryRun, settings.Delete));

            if (!string.IsNullOrWhiteSpace(result.Output))
                AnsiConsole.WriteLine(result.Output.TrimEnd());

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Sync failed:[/] {result.ErrorMessage?.EscapeMarkup() ?? "Unknown error"}");
                await NotifyAsync(config, false, settings.Target);
                return 1;
            }

            AnsiConsole.MarkupLine(settings.DryRun
                ? "[green]Sync dry-run completed; no remote files were changed.[/]"
                : "[green]Offsite sync completed.[/]");
            if (!settings.DryRun)
                await NotifyAsync(config, true, settings.Target);
            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static async Task NotifyAsync(BackupConfig config, bool success, string? target)
    {
        await NotificationConsole.SendAsync(
            config,
            success ? "sync_success" : "sync_failure",
            success,
            success ? "Offsite backup sync succeeded." : "Offsite backup sync failed.",
            target);
    }
}

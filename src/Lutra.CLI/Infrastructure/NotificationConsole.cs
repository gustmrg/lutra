using Lutra.Core.Configuration;
using Lutra.Core.Notifications;
using Spectre.Console;

namespace Lutra.CLI.Infrastructure;

internal static class NotificationConsole
{
    private static readonly IAnsiConsole ErrorConsole = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.Detect,
        ColorSystem = ColorSystemSupport.Detect,
        Out = new AnsiConsoleOutput(Console.Error)
    });

    public static async Task SendAsync(
        BackupConfig config,
        string eventName,
        bool success,
        string summary,
        string? targetName = null,
        IReadOnlyList<BackupNotificationDetail>? backups = null)
    {
        try
        {
            var service = ServiceFactory.CreateNotificationService(config);
            if (service is null)
                return;

            var errors = await service.NotifyAsync(
                NotificationEvent.Create(eventName, success, summary, targetName, backups));
            foreach (var error in errors)
            {
                ErrorConsole.MarkupLine(
                    $"[yellow]Notification warning:[/] {error.EscapeMarkup()}");
            }
        }
        catch (Exception ex)
        {
            ErrorConsole.MarkupLine(
                $"[yellow]Notification warning:[/] delivery setup failed: {ex.GetType().Name}.");
        }
    }
}

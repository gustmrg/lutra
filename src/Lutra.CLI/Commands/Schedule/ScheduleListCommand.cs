using System.Runtime.InteropServices;
using Lutra.Core.Processes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleListCommand : AsyncCommand<CommandSettings>
{
    private const string SystemdDir = "/etc/systemd/system";

    public override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AnsiConsole.MarkupLine("[red]Systemd timers are only supported on Linux.[/]");
            return 1;
        }

        if (!Directory.Exists(SystemdDir))
        {
            AnsiConsole.MarkupLine($"[red]Systemd directory not found:[/] {SystemdDir}");
            return 1;
        }

        var timerFiles = Directory.GetFiles(SystemdDir, "lutra-backup-*.timer")
            .Concat(Directory.GetFiles(SystemdDir, "lutra-verify-*.timer"))
            .OrderBy(f => f)
            .ToList();

        if (timerFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No timer units installed.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Unit");
        table.AddColumn("Schedule");
        table.AddColumn("Enabled");
        table.AddColumn("Active");

        foreach (var timerFile in timerFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(timerFile);
            var targetName = fileName.Replace("lutra-backup-", "", StringComparison.Ordinal)
                .Replace("lutra-verify-", "verify: ", StringComparison.Ordinal);

            var schedule = ParseOnCalendar(timerFile);
            var enabled = await RunSystemctlCapture("is-enabled", $"{fileName}.timer");
            var active = await RunSystemctlCapture("is-active", $"{fileName}.timer");

            var enabledMarkup = enabled == "enabled"
                ? "[green]enabled[/]"
                : "[grey]disabled[/]";

            var activeMarkup = active == "active"
                ? "[green]active[/]"
                : "[grey]inactive[/]";

            table.AddRow(targetName.EscapeMarkup(), schedule.EscapeMarkup(), enabledMarkup, activeMarkup);
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static string ParseOnCalendar(string timerFilePath)
    {
        try
        {
            foreach (var line in File.ReadLines(timerFilePath))
            {
                if (line.StartsWith("OnCalendar=", StringComparison.Ordinal))
                    return line["OnCalendar=".Length..].Trim();
            }
        }
        catch
        {
            // Non-fatal — just show unknown
        }

        return "unknown";
    }

    private static async Task<string> RunSystemctlCapture(params string[] args)
    {
        var outcome = await new SystemProcessRunner().RunAsync(new ProcessRequest("systemctl", args));
        return outcome.ExitCode == -1 ? "unknown" : outcome.StandardOutput.Trim();
    }
}

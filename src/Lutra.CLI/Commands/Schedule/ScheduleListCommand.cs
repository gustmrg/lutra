using System.Diagnostics;
using System.Runtime.InteropServices;
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
            var targetName = fileName.Replace("lutra-backup-", "", StringComparison.Ordinal);

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
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return "unknown";

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return "unknown";
        }
    }
}

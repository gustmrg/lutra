using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleRemoveCommand : AsyncCommand<ScheduleRemoveCommand.Settings>
{
    private const string SystemdDir = "/etc/systemd/system";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--target <NAME>")]
        [Description("Specific database target name. If omitted, removes all Lutra timer units.")]
        public string? Target { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
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

            var unitFiles = Directory.GetFiles(SystemdDir, "lutra-backup-*")
                .OrderBy(f => f)
                .ToList();

            if (settings.Target is not null)
            {
                var prefix = $"lutra-backup-{settings.Target}";
                unitFiles = unitFiles
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        return name == $"{prefix}.service" || name == $"{prefix}.timer";
                    })
                    .ToList();
            }

            if (unitFiles.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No matching timer units found.[/]");
                return 0;
            }

            // Stop and disable timers
            var timers = unitFiles
                .Where(f => f.EndsWith(".timer", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            foreach (var timer in timers)
            {
                await RunSystemctl("stop", timer!);
                await RunSystemctl("disable", timer!);
            }

            // Delete unit files
            foreach (var unitFile in unitFiles)
            {
                File.Delete(unitFile);
                AnsiConsole.MarkupLine($"  [green]Removed[/] {Path.GetFileName(unitFile).EscapeMarkup()}");
            }

            await RunSystemctl("daemon-reload");

            AnsiConsole.MarkupLine($"\n[green]Removed {unitFiles.Count} unit file(s).[/]");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[red]Permission denied.[/] Run this command as root (sudo).");
            return 1;
        }
    }

    private static async Task RunSystemctl(params string[] args)
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
            if (process is not null)
                await process.WaitForExitAsync();
        }
        catch
        {
            // systemctl may not be available or may fail — non-fatal
        }
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Lutra.Core.Processes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleRemoveCommand : LutraCommand<ScheduleRemoveCommand.Settings>
{
    private const string SystemdDir = "/etc/systemd/system";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--target <NAME>")]
        [Description("Specific target name. If omitted, removes all Lutra timer units.")]
        public string? Target { get; set; }
    }

    protected override async Task<int> RunAsync(Settings settings)
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
            .Concat(Directory.GetFiles(SystemdDir, "lutra-verify-*"))
            .OrderBy(f => f)
            .ToList();

        if (settings.Target is not null)
        {
            var prefixes = new[] { $"lutra-backup-{settings.Target}", $"lutra-verify-{settings.Target}" };
            unitFiles = unitFiles
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return prefixes.Any(prefix => name == $"{prefix}.service" || name == $"{prefix}.timer");
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

    // systemctl may be missing or fail; removing the unit files is what matters.
    private static Task RunSystemctl(params string[] args)
        => new SystemProcessRunner().RunAsync(new ProcessRequest("systemctl", args));
}

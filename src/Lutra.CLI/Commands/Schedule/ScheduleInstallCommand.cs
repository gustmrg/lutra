using System.Runtime.InteropServices;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleInstallCommand : LutraCommand<TargetSettings>
{
    private const string SystemdDir = "/etc/systemd/system";

    protected override Task<int> RunAsync(TargetSettings settings)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            AnsiConsole.MarkupLine("[red]Systemd timers are only supported on Linux.[/]");
            return Task.FromResult(1);
        }

        if (!Directory.Exists(SystemdDir))
        {
            AnsiConsole.MarkupLine($"[red]Systemd directory not found:[/] {SystemdDir}");
            return Task.FromResult(1);
        }

        var config = ServiceFactory.LoadConfig(settings);
        var (configPath, envPath) = ConfigFileHelper.ResolvePaths(settings.ConfigPath, settings.EnvFilePath);
        string[] commonArguments =
        [
            "--config", Path.GetFullPath(configPath),
            "--env-file", Path.GetFullPath(envPath)
        ];

        List<DatabaseTarget> targets = settings.Target is not null
            ? [ServiceFactory.ResolveTarget(config, settings.Target)]
            : config.Databases;

        foreach (var target in targets)
        {
            InstallUnit(
                $"lutra-backup-{target.Name}",
                $"Lutra backup for {target.Name}",
                target.Schedule,
                ["backup", "run", "--target", target.Name, .. commonArguments]);

            if (target.VerifySchedule is not null)
            {
                InstallUnit(
                    $"lutra-verify-{target.Name}",
                    $"Lutra restore drill for {target.Name}",
                    target.VerifySchedule,
                    ["verify", "--target", target.Name, .. commonArguments]);
            }
        }

        AnsiConsole.MarkupLine($"\nRun [blue]sudo systemctl daemon-reload[/] to load the new units.");
        AnsiConsole.MarkupLine("Enable timers with [blue]sudo systemctl enable --now <unit>.timer[/]");
        return Task.FromResult(0);
    }

    private static void InstallUnit(string unitName, string description, string schedule, string[] arguments)
    {
        string[] commandLine = [Environment.ProcessPath ?? "lutra", .. arguments];
        var command = string.Join(' ', commandLine.Select(Quote));

        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.service"), $"""
            [Unit]
            Description={description}

            [Service]
            Type=oneshot
            ExecStart={command}
            """);

        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.timer"), $"""
            [Unit]
            Description={description} (timer)

            [Timer]
            OnCalendar={schedule}
            Persistent=true

            [Install]
            WantedBy=timers.target
            """);

        AnsiConsole.MarkupLine($"  [green]Installed[/] {unitName}.timer ({schedule.EscapeMarkup()})");
    }

    // systemd splits ExecStart on whitespace and expands % and $ sequences, so an
    // argument with any character outside the plain set is quoted and escaped.
    private static string Quote(string argument)
    {
        const string plain = "/._-:@+=,";
        if (argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || plain.Contains(c)))
            return argument;

        var escaped = argument
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("%", "%%")
            .Replace("$", "$$");
        return $"\"{escaped}\"";
    }
}

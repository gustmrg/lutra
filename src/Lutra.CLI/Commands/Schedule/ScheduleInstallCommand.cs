using System.Runtime.InteropServices;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleInstallCommand : LutraCommand<ScheduleInstallSettings>
{
    private const string SystemdDir = "/etc/systemd/system";

    protected override Task<int> RunAsync(ScheduleInstallSettings settings)
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

        var user = ResolveServiceUser(settings.User);
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
                user,
                ["backup", "run", "--target", target.Name, .. commonArguments]);

            if (target.VerifySchedule is not null)
            {
                InstallUnit(
                    $"lutra-verify-{target.Name}",
                    $"Lutra restore drill for {target.Name}",
                    target.VerifySchedule,
                    user,
                    ["verify", "--target", target.Name, .. commonArguments]);
            }
        }

        AnsiConsole.MarkupLine(
            $"\nTimers run as [blue]{user.EscapeMarkup()}[/]. That account needs Docker access and must own " +
            "the backup and state directories.");
        AnsiConsole.MarkupLine($"Run [blue]sudo systemctl daemon-reload[/] to load the new units.");
        AnsiConsole.MarkupLine("Enable timers with [blue]sudo systemctl enable --now <unit>.timer[/]");
        return Task.FromResult(0);
    }

    // A unit without User= runs as root, which leaves root-owned backups and state
    // behind for an installation otherwise operated by an ordinary account. The
    // account is therefore always written out, and never guessed to be root.
    private static string ResolveServiceUser(string? requested)
    {
        var user = requested ?? Environment.GetEnvironmentVariable("SUDO_USER");
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new ConfigurationException(
                "Cannot tell which account the timers should run as. Pass --user <name> " +
                "(use --user root to run them as root).");
        }

        var exists = File.ReadLines("/etc/passwd").Any(line => line.StartsWith(user + ":", StringComparison.Ordinal));
        if (!exists)
            throw new ConfigurationException($"Account '{user}' does not exist on this system.");

        return user;
    }

    private static void InstallUnit(
        string unitName, string description, string schedule, string user, string[] arguments)
    {
        string[] commandLine = [Environment.ProcessPath ?? "lutra", .. arguments];
        var command = string.Join(' ', commandLine.Select(Quote));

        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.service"), $"""
            [Unit]
            Description={description}

            [Service]
            Type=oneshot
            User={user}
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

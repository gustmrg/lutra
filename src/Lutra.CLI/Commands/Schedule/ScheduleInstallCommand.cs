using System.Runtime.InteropServices;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleInstallCommand : AsyncCommand<TargetSettings>
{
    private const string SystemdDir = "/etc/systemd/system";

    public override Task<int> ExecuteAsync(CommandContext context, TargetSettings settings)
    {
        try
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
            var lutraPath = Environment.ProcessPath ?? "lutra";
            var resolvedConfigPath = ConfigFileHelper.ResolveConfigPath(settings.ConfigPath);
            var resolvedEnvPath = ConfigFileHelper.ResolveEnvPath(settings.EnvFilePath);

            var targets = settings.Target is not null
                ? [ServiceFactory.ResolveTarget(config, settings.Target)]
                : config.Databases;

            foreach (var target in targets)
            {
                var unitName = $"lutra-backup-{target.Name}";
                InstallUnit(unitName, target, lutraPath, resolvedConfigPath, resolvedEnvPath);
                AnsiConsole.MarkupLine($"  [green]Installed[/] {unitName}.timer ({target.Schedule.EscapeMarkup()})");
            }

            AnsiConsole.MarkupLine($"\nRun [blue]sudo systemctl daemon-reload[/] to load the new units.");
            AnsiConsole.MarkupLine("Enable timers with [blue]sudo systemctl enable --now <unit>.timer[/]");
            return Task.FromResult(0);
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return Task.FromResult(1);
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[red]Permission denied.[/] Run this command as root (sudo).");
            return Task.FromResult(1);
        }
    }

    private static void InstallUnit(string unitName, DatabaseTarget target, string lutraPath, string configPath, string envFilePath)
    {
        var serviceContent = $"""
            [Unit]
            Description=Lutra backup for {target.Name}

            [Service]
            Type=oneshot
            ExecStart={lutraPath} backup run --target {target.Name} --config {configPath} --env-file {envFilePath}
            """;

        var timerContent = $"""
            [Unit]
            Description=Lutra backup timer for {target.Name}

            [Timer]
            OnCalendar={target.Schedule}
            Persistent=true

            [Install]
            WantedBy=timers.target
            """;

        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.service"), serviceContent);
        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.timer"), timerContent);
    }
}

using System.Runtime.InteropServices;
using Lutra.CLI.Commands.Config;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Lutra.Core.Recovery;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleInstallCommand : AsyncCommand<ScheduleInstallSettings>
{
    private const string SystemdDir = "/etc/systemd/system";

    public override Task<int> ExecuteAsync(CommandContext context, ScheduleInstallSettings settings)
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
            var serviceUser = SystemdServiceUser.Resolve(settings.RunAsUser);
            var lutraPath = Environment.ProcessPath ?? "lutra";
            var (resolvedConfigPath, resolvedEnvPath) = ConfigFileHelper.ResolvePaths(
                settings.ConfigPath,
                settings.EnvFilePath);

            var targets = settings.Target is not null
                ? new List<IBackupTarget> { ServiceFactory.ResolveTarget(config, settings.Target) }
                : config.AllTargets().ToList();

            foreach (var target in targets)
            {
                var unitName = $"lutra-backup-{target.Name}";
                InstallUnit(unitName, target, lutraPath, resolvedConfigPath, resolvedEnvPath, serviceUser);
                AnsiConsole.MarkupLine(
                    $"  [green]Installed[/] {unitName}.timer ({target.Schedule.EscapeMarkup()}, runs as {serviceUser.Name.EscapeMarkup()})");

                if (target is DatabaseTarget { VerifySchedule: not null } database)
                {
                    InstallVerifyUnit(database, lutraPath, resolvedConfigPath, resolvedEnvPath, serviceUser);
                    AnsiConsole.MarkupLine($"  [green]Installed[/] lutra-verify-{database.Name}.timer ({database.VerifySchedule!.EscapeMarkup()})");
                }
            }

            if (settings.Target is null && config.Inventory is { Enabled: true } inventory)
            {
                InstallInventoryUnit(lutraPath, resolvedConfigPath, resolvedEnvPath, inventory.Schedule, serviceUser);
                AnsiConsole.MarkupLine($"  [green]Installed[/] lutra-inventory.timer ({inventory.Schedule.EscapeMarkup()})");
            }

            if (settings.Target is null && config.Environment is { Enabled: true } environment)
            {
                InstallEnvironmentUnit(lutraPath, resolvedConfigPath, resolvedEnvPath, environment.Schedule, serviceUser);
                AnsiConsole.MarkupLine(
                    $"  [green]Installed[/] {EnvironmentScheduleUnits.UnitName}.timer ({environment.Schedule.EscapeMarkup()})");
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

    private static void InstallVerifyUnit(
        DatabaseTarget target,
        string lutraPath,
        string configPath,
        string envFilePath,
        SystemdServiceUser serviceUser)
    {
        var unitName = $"lutra-verify-{target.Name}";
        var serviceContent = SystemdServiceUnit.Build(
            $"Lutra restore drill for {target.Name}",
            serviceUser,
            lutraPath,
            "verify", "--target", target.Name, "--config", configPath, "--env-file", envFilePath);
        var timerContent = $"""
            [Unit]
            Description=Lutra restore drill timer for {target.Name}

            [Timer]
            OnCalendar={target.VerifySchedule}
            Persistent=true

            [Install]
            WantedBy=timers.target
            """;
        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.service"), serviceContent);
        File.WriteAllText(Path.Combine(SystemdDir, $"{unitName}.timer"), timerContent);
    }

    private static void InstallInventoryUnit(
        string lutraPath,
        string configPath,
        string envFilePath,
        string schedule,
        SystemdServiceUser serviceUser)
    {
        var serviceContent = SystemdServiceUnit.Build(
            "Lutra server inventory snapshot",
            serviceUser,
            lutraPath,
            "inventory", "--config", configPath, "--env-file", envFilePath);

        var timerContent = $"""
            [Unit]
            Description=Lutra server inventory timer

            [Timer]
            OnCalendar={schedule}
            Persistent=true

            [Install]
            WantedBy=timers.target
            """;

        File.WriteAllText(Path.Combine(SystemdDir, "lutra-inventory.service"), serviceContent);
        File.WriteAllText(Path.Combine(SystemdDir, "lutra-inventory.timer"), timerContent);
    }

    private static void InstallUnit(
        string unitName,
        IBackupTarget target,
        string lutraPath,
        string configPath,
        string envFilePath,
        SystemdServiceUser serviceUser)
    {
        var serviceContent = SystemdServiceUnit.Build(
            $"Lutra backup for {target.Name}",
            serviceUser,
            lutraPath,
            "backup", "run", "--target", target.Name, "--config", configPath, "--env-file", envFilePath);

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

    private static void InstallEnvironmentUnit(
        string lutraPath,
        string configPath,
        string envFilePath,
        string schedule,
        SystemdServiceUser serviceUser)
    {
        var content = EnvironmentScheduleUnits.Build(
            lutraPath,
            configPath,
            envFilePath,
            schedule,
            serviceUser.Name,
            serviceUser.HomeDirectory);
        File.WriteAllText(
            Path.Combine(SystemdDir, EnvironmentScheduleUnits.UnitName + ".service"), content.Service);
        File.WriteAllText(
            Path.Combine(SystemdDir, EnvironmentScheduleUnits.UnitName + ".timer"), content.Timer);
    }
}

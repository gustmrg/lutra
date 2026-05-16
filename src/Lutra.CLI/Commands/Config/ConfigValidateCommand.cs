using System.Diagnostics;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigValidateCommand : AsyncCommand<ConfigValidateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigValidateSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);

            AnsiConsole.MarkupLine("[green]Configuration is valid.[/]");
            AnsiConsole.MarkupLine($"  Backup directory: [blue]{config.BackupDirectory.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine($"  Retention: max_count={config.Retention.MaxCount}, max_age_days={config.Retention.MaxAgeDays}");
            AnsiConsole.MarkupLine($"  Database targets: [blue]{config.Databases.Count}[/]");

            foreach (var db in config.Databases)
            {
                AnsiConsole.MarkupLine($"    - {db.Name.EscapeMarkup()} ({db.Type}, container: {db.Container.EscapeMarkup()})");
            }

            if (!CheckBackupDirectory(config.BackupDirectory))
                return 1;

            if (settings.Preflight)
                return await RunPreflightAsync(config);

            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static bool CheckBackupDirectory(string backupDirectory)
    {
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var probePath = Path.Combine(backupDirectory, $".lutra-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probePath, "");
            File.Delete(probePath);
            AnsiConsole.MarkupLine("  Backup directory: [green]writable[/]");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Backup directory is not writable:[/] {ex.Message.EscapeMarkup()}");
            return false;
        }
    }

    private static async Task<int> RunPreflightAsync(BackupConfig config)
    {
        var failed = false;

        if (!await CommandSucceedsAsync("docker", ["version", "--format", "{{.Server.Version}}"]))
        {
            AnsiConsole.MarkupLine("[red]Docker daemon is not reachable.[/]");
            failed = true;
        }
        else
        {
            AnsiConsole.MarkupLine("  Docker daemon: [green]reachable[/]");
        }

        foreach (var db in config.Databases)
        {
            if (!await ValidateScheduleAsync(db))
                failed = true;

            if (!await CommandSucceedsAsync("docker", ["inspect", "-f", "{{.State.Running}}", db.Container]))
            {
                AnsiConsole.MarkupLine($"[red]{db.Name.EscapeMarkup()}:[/] container '{db.Container.EscapeMarkup()}' is not running or does not exist.");
                failed = true;
                continue;
            }

            var tool = GetRequiredTool(db.Type);
            if (!await CommandSucceedsAsync("docker", ["exec", db.Container, "sh", "-lc", $"command -v {tool} >/dev/null 2>&1"]))
            {
                AnsiConsole.MarkupLine($"[red]{db.Name.EscapeMarkup()}:[/] required dump tool '{tool.EscapeMarkup()}' was not found in the container.");
                failed = true;
            }
            else
            {
                AnsiConsole.MarkupLine($"  {db.Name.EscapeMarkup()}: [green]container and dump tool available[/]");
            }
        }

        return failed ? 1 : 0;
    }

    private static async Task<bool> ValidateScheduleAsync(DatabaseTarget db)
    {
        if (!await CommandExistsAsync("systemd-analyze"))
        {
            AnsiConsole.MarkupLine($"  {db.Name.EscapeMarkup()}: [yellow]skipped systemd schedule validation; systemd-analyze not found[/]");
            return true;
        }

        var ok = await CommandSucceedsAsync("systemd-analyze", ["calendar", db.Schedule]);
        if (!ok)
            AnsiConsole.MarkupLine($"[red]{db.Name.EscapeMarkup()}:[/] invalid systemd calendar expression '{db.Schedule.EscapeMarkup()}'.");

        return ok;
    }

    private static string GetRequiredTool(DatabaseType type) => type switch
    {
        DatabaseType.PostgreSql => "pg_dump",
        DatabaseType.MongoDb => "mongodump",
        DatabaseType.SqlServer => "/opt/mssql-tools18/bin/sqlcmd",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static async Task<bool> CommandExistsAsync(string command)
    {
        return await CommandSucceedsAsync("sh", ["-lc", $"command -v {command} >/dev/null 2>&1"]);
    }

    private static async Task<bool> CommandSucceedsAsync(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

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

            AnsiConsole.MarkupLine($"  File targets: [blue]{config.Files.Count}[/]");

            foreach (var ft in config.Files)
            {
                AnsiConsole.MarkupLine($"    - {ft.Name.EscapeMarkup()} ({ft.Paths.Count} path(s))");
            }

            if (config.Inventory is { } inventory)
            {
                var collectors = inventory.Collectors is null ? "all collectors" : string.Join(", ", inventory.Collectors);
                AnsiConsole.MarkupLine($"  Inventory: {(inventory.Enabled ? "[green]enabled[/]" : "[grey]disabled[/]")} ({collectors.EscapeMarkup()})");
            }

            if (config.Sync is { } sync)
                AnsiConsole.MarkupLine($"  Offsite sync: [blue]{sync.User.EscapeMarkup()}@{sync.Host.EscapeMarkup()}:{sync.DestinationPath.EscapeMarkup()}[/]");

            if (!CheckBackupDirectory(config.BackupDirectory))
                return 1;

            if (!CheckFileTargets(config))
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

        foreach (var ft in config.Files)
        {
            if (!await ValidateScheduleAsync(ft))
                failed = true;
        }

        if (config.Inventory is { Enabled: true } inventory
            && !await ValidateScheduleExpressionAsync("inventory", inventory.Schedule))
            failed = true;

        if (config.Sync is not null)
        {
            var syncValidation = await ServiceFactory.CreateRsyncService(config).ValidateAsync();
            if (syncValidation.Success)
                AnsiConsole.MarkupLine("  Offsite sync: [green]reachable and writable[/]");
            else
            {
                AnsiConsole.MarkupLine($"[red]Offsite sync:[/] {syncValidation.Message.EscapeMarkup()}");
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    private static bool CheckFileTargets(BackupConfig config)
    {
        var valid = true;

        foreach (var ft in config.Files)
        {
            foreach (var path in ft.Paths)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    AnsiConsole.MarkupLine($"[red]{ft.Name.EscapeMarkup()}:[/] path does not exist: {path.EscapeMarkup()}");
                    valid = false;
                    continue;
                }

                if (!IsReadable(path))
                {
                    AnsiConsole.MarkupLine($"[red]{ft.Name.EscapeMarkup()}:[/] path is not readable: {path.EscapeMarkup()}");
                    valid = false;
                    continue;
                }

                foreach (var sensitive in FindSensitivePaths(path))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]{ft.Name.EscapeMarkup()}:[/] '{sensitive.EscapeMarkup()}' looks like it may contain secrets. " +
                        "Backups are stored unencrypted; restrict access to the backup directory (encryption is planned).");
                }
            }
        }

        return valid;
    }

    private static bool IsReadable(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return true;
            }

            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> FindSensitivePaths(string path)
    {
        if (LooksSensitive(path))
        {
            yield return path;
            yield break;
        }

        // Also scan the immediate children of a configured directory, since users
        // typically add whole directories (e.g. an app folder containing .env).
        if (!Directory.Exists(path))
            yield break;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (LooksSensitive(child))
                yield return child;
        }
    }

    private static bool LooksSensitive(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant();

        return name.Contains(".env")
            || name is "id_rsa" or "id_ed25519" or "id_ecdsa" or "id_dsa"
            || name.EndsWith(".pem")
            || name.EndsWith(".key")
            || name.EndsWith(".p12")
            || name.EndsWith(".pfx")
            || name.Contains("secret")
            || name.Contains("credential");
    }

    private static Task<bool> ValidateScheduleAsync(IBackupTarget target)
        => ValidateScheduleExpressionAsync(target.Name, target.Schedule);

    private static async Task<bool> ValidateScheduleExpressionAsync(string name, string schedule)
    {
        if (!await CommandExistsAsync("systemd-analyze"))
        {
            AnsiConsole.MarkupLine($"  {name.EscapeMarkup()}: [yellow]skipped systemd schedule validation; systemd-analyze not found[/]");
            return true;
        }

        var ok = await CommandSucceedsAsync("systemd-analyze", ["calendar", schedule]);
        if (!ok)
            AnsiConsole.MarkupLine($"[red]{name.EscapeMarkup()}:[/] invalid systemd calendar expression '{schedule.EscapeMarkup()}'.");

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

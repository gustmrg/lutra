using System.Diagnostics;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Lutra.Core.Persistence;
using Microsoft.Data.Sqlite;
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
            AnsiConsole.MarkupLine($"  State directory: [blue]{config.StateDirectory!.EscapeMarkup()}[/]");
            if (config.UsesStateDirectoryCompatibilityFallback)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]State directory compatibility fallback:[/] this custom configuration stores state " +
                    "under the backup directory. Set an explicit absolute [blue]state_directory[/] to keep local state separate.");
            }
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

            AnsiConsole.MarkupLine($"  Volume targets: [blue]{config.Volumes.Count}[/]");
            foreach (var volume in config.Volumes)
                AnsiConsole.MarkupLine($"    - {volume.Name.EscapeMarkup()} ({volume.Volume.EscapeMarkup()})");

            if (config.Inventory is { } inventory)
            {
                var collectors = inventory.Collectors is null ? "all collectors" : string.Join(", ", inventory.Collectors);
                AnsiConsole.MarkupLine($"  Inventory: {(inventory.Enabled ? "[green]enabled[/]" : "[grey]disabled[/]")} ({collectors.EscapeMarkup()})");
            }

            if (config.Environment is { } environment)
            {
                AnsiConsole.MarkupLine(
                    $"  Environment recovery: {(environment.Enabled ? "[green]enabled[/]" : "[grey]disabled[/]")} " +
                    $"({environment.Targets.Count} target(s), plaintext)");
                if (environment.Enabled)
                    AnsiConsole.MarkupLine("  [yellow]Warning:[/] recovery sets exclude common secrets but are not encrypted.");
            }

            if (config.Sync is { } sync)
                AnsiConsole.MarkupLine($"  Offsite sync: [blue]{sync.User.EscapeMarkup()}@{sync.Host.EscapeMarkup()}:{sync.DestinationPath.EscapeMarkup()}[/]");

            if (config.Notifications?.Discord is { } discord)
                AnsiConsole.MarkupLine($"  Discord notifications: [blue]{discord.Webhooks.Count} webhook(s)[/]");

            if (!CheckBackupDirectory(config.BackupDirectory))
                return 1;

            if (!CheckStateDatabase(config))
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
        catch (LutraDatabaseOwnershipException ex)
        {
            AnsiConsole.MarkupLine($"[red]State ownership conflict:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "Use a distinct explicit [blue]state_directory[/] for this configuration; Lutra will not mix unrelated application state.");
            return 1;
        }
    }

    private static bool CheckStateDatabase(BackupConfig config)
    {
        try
        {
            var database = new LutraDatabase(
                config.StateDirectory!,
                config.ConfigPath!,
                config.BackupDirectory);
            database.ProbeWriteAccess();
            AnsiConsole.MarkupLine("  Application database: [green]writable (SQLite WAL)[/]");
            return true;
        }
        catch (LutraDatabaseOwnershipException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine(
                $"[red]Application state is not writable:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                $"The current OS account must be able to create and open lutra.db, lutra.db-wal, and lutra.db-shm in " +
                $"'{config.StateDirectory!.EscapeMarkup()}'. Fix directory ownership/permissions or choose another state_directory.");
            return false;
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
            if (db.VerifySchedule is not null
                && !await ValidateScheduleExpressionAsync($"{db.Name} restore drill", db.VerifySchedule))
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
                var versionArgs = db.Type switch
                {
                    DatabaseType.PostgreSql => new[] { "exec", db.Container, "pg_dump", "--version" },
                    DatabaseType.MongoDb => new[] { "exec", db.Container, "mongodump", "--version" },
                    DatabaseType.SqlServer => new[] { "exec", db.Container, "/opt/mssql-tools18/bin/sqlcmd", "-?" },
                    DatabaseType.SQLite => new[] { "exec", db.Container, "sqlite3", "--version" },
                    _ => []
                };
                var version = await CommandOutputAsync("docker", versionArgs);
                if (!string.IsNullOrWhiteSpace(version))
                    AnsiConsole.MarkupLine($"    Tool version: [grey]{version.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().EscapeMarkup()}[/]");
            }
        }

        foreach (var ft in config.Files)
        {
            if (!await ValidateScheduleAsync(ft))
                failed = true;
        }

        foreach (var volume in config.Volumes)
        {
            if (!await ValidateScheduleAsync(volume))
                failed = true;
            if (!await CommandSucceedsAsync("docker", ["volume", "inspect", volume.Volume]))
            {
                AnsiConsole.MarkupLine($"[red]{volume.Name.EscapeMarkup()}:[/] Docker volume '{volume.Volume.EscapeMarkup()}' does not exist.");
                failed = true;
            }
            else
                AnsiConsole.MarkupLine($"  {volume.Name.EscapeMarkup()}: [green]Docker volume exists[/]");
        }

        if (config.Inventory is { Enabled: true } inventory
            && !await ValidateScheduleExpressionAsync("inventory", inventory.Schedule))
            failed = true;

        if (config.Environment is { Enabled: true } environment)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]Environment recovery:[/] plaintext output; common secret paths in file targets are always excluded.");
            if (!await ValidateScheduleExpressionAsync("environment recovery", environment.Schedule))
                failed = true;
            if (!CheckEnvironmentOutputDirectory(config))
                failed = true;
        }

        if (config.AllTargets().Any(target => target.Encryption is not null) || config.Encryption is not null)
        {
            if (!await CommandSucceedsAsync("age", ["--version"]))
            {
                AnsiConsole.MarkupLine("[red]Encryption:[/] age is configured but the 'age' executable was not found.");
                failed = true;
            }
            else
                AnsiConsole.MarkupLine("  Encryption: [green]age is available[/]");
        }

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

    private static bool CheckEnvironmentOutputDirectory(BackupConfig config)
    {
        var directory = Path.Combine(config.BackupDirectory, "environment");
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(directory, mode);
                File.SetUnixFileMode(directory, mode);
                if (File.GetUnixFileMode(directory) != mode)
                    throw new UnauthorizedAccessException();
            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            var probe = Path.Combine(directory, $".lutra-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            AnsiConsole.MarkupLine("  Environment output: [green]writable and private[/]");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[red]Environment output:[/] cannot create a private writable directory.");
            return false;
        }
    }

    private static bool CheckFileTargets(BackupConfig config)
    {
        var valid = true;

        foreach (var database in config.Databases.Where(database => database.PostgresWalArchivePath is not null))
        {
            var path = database.PostgresWalArchivePath!;
            if (!Directory.Exists(path) || !IsReadable(path))
            {
                AnsiConsole.MarkupLine($"[red]{database.Name.EscapeMarkup()}:[/] PostgreSQL WAL archive path is missing or unreadable: {path.EscapeMarkup()}");
                valid = false;
            }
        }

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
                    if (ft.Encryption is null && config.Encryption is null)
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]{ft.Name.EscapeMarkup()}:[/] '{sensitive.EscapeMarkup()}' looks like it may contain secrets. " +
                            "Configure age encryption or restrict access to the backup directory.");
                    }
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
        DatabaseType.SQLite => "sqlite3",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static async Task<bool> CommandExistsAsync(string command)
    {
        return await CommandSucceedsAsync("sh", ["-lc", $"command -v {command} >/dev/null 2>&1"]);
    }

    private static async Task<string?> CommandOutputAsync(string fileName, IReadOnlyList<string> args)
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
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdout;
            if (string.IsNullOrWhiteSpace(output))
                output = await stderr;
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
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

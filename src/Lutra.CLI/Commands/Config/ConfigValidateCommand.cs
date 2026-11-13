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

            if (config.Sync is { } sync)
                AnsiConsole.MarkupLine($"  Offsite sync: [blue]{sync.User.EscapeMarkup()}@{sync.Host.EscapeMarkup()}:{sync.DestinationPath.EscapeMarkup()}[/]");

            if (config.Notifications?.Discord is { } discord)
                AnsiConsole.MarkupLine($"  Discord notifications: [blue]{discord.Webhooks.Count} webhook(s)[/]");

            if (!CheckBackupDirectory(config.BackupDirectory))
                return 1;

            if (!CheckStateDatabase(config))
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
                config.ConfigPath!);
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

            if (!await CommandSucceedsAsync("docker", ["exec", db.Container, "sh", "-lc", "command -v pg_dump >/dev/null 2>&1"]))
            {
                AnsiConsole.MarkupLine($"[red]{db.Name.EscapeMarkup()}:[/] required dump tool 'pg_dump' was not found in the container.");
                failed = true;
            }
            else
            {
                AnsiConsole.MarkupLine($"  {db.Name.EscapeMarkup()}: [green]container and dump tool available[/]");
                var version = await CommandOutputAsync("docker", ["exec", db.Container, "pg_dump", "--version"]);
                if (!string.IsNullOrWhiteSpace(version))
                    AnsiConsole.MarkupLine($"    Tool version: [grey]{version.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().EscapeMarkup()}[/]");
            }
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

    private static Task<bool> ValidateScheduleAsync(DatabaseTarget target)
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

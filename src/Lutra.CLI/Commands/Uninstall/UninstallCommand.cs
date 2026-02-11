using System.Diagnostics;
using System.Runtime.InteropServices;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Uninstall;

public sealed class UninstallCommand : AsyncCommand<UninstallSettings>
{
    private const string SystemdDir = "/etc/systemd/system";

    public override async Task<int> ExecuteAsync(CommandContext context, UninstallSettings settings)
    {
        try
        {
            if (!settings.Yes && !AnsiConsole.Profile.Capabilities.Interactive)
            {
                AnsiConsole.MarkupLine("[red]Non-interactive terminal detected.[/] Pass [blue]--yes[/] to skip prompts.");
                return 1;
            }

            // Discover artifacts
            var binaryPath = Environment.ProcessPath;
            var configDir = ConfigTemplates.GetDefaultConfigDirectory();
            var backupDir = ConfigTemplates.GetDefaultBackupDirectory();

            // Try to read backup_directory from config if it exists
            var configPath = Path.Combine(configDir, "lutra.yaml");
            if (File.Exists(configPath))
            {
                try
                {
                    var loader = new YamlConfigLoader();
                    var config = loader.Load(configPath);
                    backupDir = config.BackupDirectory;
                }
                catch
                {
                    // Fall back to default
                }
            }

            var systemdUnits = FindSystemdUnits();
            var configDirExists = Directory.Exists(configDir);
            var backupDirExists = Directory.Exists(backupDir);

            // Display summary
            AnsiConsole.MarkupLine("[bold]Lutra will remove the following:[/]");
            AnsiConsole.WriteLine();

            if (systemdUnits.Count > 0)
            {
                AnsiConsole.MarkupLine($"  Systemd units: [blue]{systemdUnits.Count}[/] (timers + services)");
                foreach (var unit in systemdUnits)
                    AnsiConsole.MarkupLine($"    {Path.GetFileName(unit).EscapeMarkup()}");
            }

            if (configDirExists)
                AnsiConsole.MarkupLine($"  Config directory: [blue]{configDir.EscapeMarkup()}[/]");

            if (backupDirExists && !settings.KeepBackups)
                AnsiConsole.MarkupLine($"  Backup directory: [blue]{backupDir.EscapeMarkup()}[/]");
            else if (backupDirExists && settings.KeepBackups)
                AnsiConsole.MarkupLine($"  Backup directory: [yellow]kept[/] (--keep-backups)");

            if (binaryPath is not null)
                AnsiConsole.MarkupLine($"  Binary: [blue]{binaryPath.EscapeMarkup()}[/]");
            else
                AnsiConsole.MarkupLine("  Binary: [yellow]unknown path, skipping[/]");

            if (!configDirExists && !backupDirExists && systemdUnits.Count == 0 && binaryPath is null)
            {
                AnsiConsole.MarkupLine("\n[yellow]Nothing to remove.[/]");
                return 0;
            }

            AnsiConsole.WriteLine();

            // Confirm
            if (!settings.Yes)
            {
                if (!AnsiConsole.Confirm("Proceed with uninstall?", defaultValue: false))
                {
                    AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                    return 0;
                }
            }

            // Ask about backups separately if they exist and --keep-backups not set
            var deleteBackups = false;
            if (backupDirExists && !settings.KeepBackups)
            {
                if (settings.Yes)
                {
                    deleteBackups = true;
                }
                else
                {
                    deleteBackups = AnsiConsole.Confirm(
                        "Delete backup data? This cannot be undone.", defaultValue: false);
                }
            }

            AnsiConsole.WriteLine();
            var removed = new List<string>();
            var skipped = new List<string>();

            // 1. Stop & disable systemd timers, remove unit files
            if (systemdUnits.Count > 0)
                await RemoveSystemdUnits(systemdUnits, removed, skipped);

            // 2. Remove config directory
            if (configDirExists)
            {
                try
                {
                    Directory.Delete(configDir, recursive: true);
                    removed.Add($"Config directory: {configDir}");
                    AnsiConsole.MarkupLine($"  [green]Removed[/] {configDir.EscapeMarkup()}");
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add($"Config directory: {configDir} (permission denied)");
                    AnsiConsole.MarkupLine($"  [yellow]Skipped[/] {configDir.EscapeMarkup()} (permission denied)");
                    AnsiConsole.MarkupLine($"  Run: [blue]sudo rm -rf {configDir.EscapeMarkup()}[/]");
                }
            }

            // 3. Remove backup directory
            if (deleteBackups)
            {
                try
                {
                    Directory.Delete(backupDir, recursive: true);
                    removed.Add($"Backup directory: {backupDir}");
                    AnsiConsole.MarkupLine($"  [green]Removed[/] {backupDir.EscapeMarkup()}");
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add($"Backup directory: {backupDir} (permission denied)");
                    AnsiConsole.MarkupLine($"  [yellow]Skipped[/] {backupDir.EscapeMarkup()} (permission denied)");
                    AnsiConsole.MarkupLine($"  Run: [blue]sudo rm -rf {backupDir.EscapeMarkup()}[/]");
                }
            }
            else if (backupDirExists)
            {
                skipped.Add($"Backup directory: {backupDir} (preserved)");
            }

            // 4. Delete binary (last step — safe on Linux, process keeps running)
            if (binaryPath is not null)
            {
                try
                {
                    File.Delete(binaryPath);
                    removed.Add($"Binary: {binaryPath}");
                    AnsiConsole.MarkupLine($"  [green]Removed[/] {binaryPath.EscapeMarkup()}");
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add($"Binary: {binaryPath} (permission denied)");
                    AnsiConsole.MarkupLine($"  [yellow]Skipped[/] {binaryPath.EscapeMarkup()} (permission denied)");
                    AnsiConsole.MarkupLine($"  Run: [blue]sudo rm {binaryPath.EscapeMarkup()}[/]");
                }
            }

            // Print summary
            AnsiConsole.WriteLine();
            if (removed.Count > 0)
                AnsiConsole.MarkupLine($"[green]Removed {removed.Count} item(s).[/]");
            if (skipped.Count > 0)
                AnsiConsole.MarkupLine($"[yellow]Skipped {skipped.Count} item(s).[/]");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static List<string> FindSystemdUnits()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !Directory.Exists(SystemdDir))
            return [];

        try
        {
            return Directory.GetFiles(SystemdDir, "lutra-backup-*")
                .OrderBy(f => f)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task RemoveSystemdUnits(
        List<string> unitFiles, List<string> removed, List<string> skipped)
    {
        // Find timer units to stop/disable
        var timers = unitFiles
            .Where(f => f.EndsWith(".timer", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        foreach (var timer in timers)
        {
            await RunSystemctl("stop", timer!);
            await RunSystemctl("disable", timer!);
        }

        // Remove unit files
        foreach (var unitFile in unitFiles)
        {
            try
            {
                File.Delete(unitFile);
                removed.Add($"Systemd unit: {Path.GetFileName(unitFile)}");
                AnsiConsole.MarkupLine($"  [green]Removed[/] {unitFile.EscapeMarkup()}");
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add($"Systemd unit: {Path.GetFileName(unitFile)} (permission denied)");
                AnsiConsole.MarkupLine($"  [yellow]Skipped[/] {unitFile.EscapeMarkup()} (permission denied)");
            }
        }

        // Daemon reload
        if (removed.Any(r => r.StartsWith("Systemd unit:")))
            await RunSystemctl("daemon-reload");
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

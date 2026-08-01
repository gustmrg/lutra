using System.Diagnostics;
using System.Runtime.InteropServices;
using Lutra.CLI.Commands.Config;
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
            var configPath = Path.GetFullPath(ConfigFileHelper.ResolveConfigPath(settings.ConfigPath));
            var configDir = Path.GetDirectoryName(configPath)!;
            var removeWholeConfigDirectory =
                UninstallDataPolicy.ShouldRemoveWholeConfigDirectory(settings.ConfigPath);
            var backupDir = ConfigTemplates.GetDefaultBackupDirectory();
            var stateDir = ConfigTemplates.GetDefaultStateDirectory();
            var dataDirectoriesResolved = string.IsNullOrWhiteSpace(settings.ConfigPath);

            // Prefer the resolved paths from the selected installation config.
            if (File.Exists(configPath))
            {
                try
                {
                    var loader = new YamlConfigLoader();
                    var config = loader.Load(configPath);
                    backupDir = Path.GetFullPath(config.BackupDirectory, configDir);
                    stateDir = config.StateDirectory!;
                    dataDirectoriesResolved = true;
                }
                catch (Exception ex)
                {
                    dataDirectoriesResolved = false;
                    AnsiConsole.MarkupLine(
                        $"[yellow]Could not resolve backup/state directories from '{configPath.EscapeMarkup()}':[/] " +
                        $"{ex.Message.EscapeMarkup()}. Data directories will be preserved for manual review.");
                }
            }
            else if (!removeWholeConfigDirectory)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Selected configuration was not found:[/] {configPath.EscapeMarkup()}. " +
                    "Data directories will be preserved for manual review.");
            }

            var systemdUnits = FindSystemdUnits();
            var configItemExists = removeWholeConfigDirectory
                ? Directory.Exists(configDir)
                : File.Exists(configPath);
            var backupDirExists = dataDirectoriesResolved && Directory.Exists(backupDir);
            var stateDirExists = dataDirectoriesResolved && Directory.Exists(stateDir);

            // Display summary
            AnsiConsole.MarkupLine("[bold]Lutra will remove the following:[/]");
            AnsiConsole.WriteLine();

            if (systemdUnits.Count > 0)
            {
                AnsiConsole.MarkupLine($"  Systemd units: [blue]{systemdUnits.Count}[/] (timers + services)");
                foreach (var unit in systemdUnits)
                    AnsiConsole.MarkupLine($"    {Path.GetFileName(unit).EscapeMarkup()}");
            }

            if (configItemExists && removeWholeConfigDirectory)
                AnsiConsole.MarkupLine($"  Config directory: [blue]{configDir.EscapeMarkup()}[/]");
            else if (configItemExists)
                AnsiConsole.MarkupLine($"  Config file: [blue]{configPath.EscapeMarkup()}[/]");

            if (backupDirExists && UninstallDataPolicy.ShouldDeleteBackups(settings.KeepBackups))
                AnsiConsole.MarkupLine($"  Backup directory: [blue]{backupDir.EscapeMarkup()}[/]");
            else if (backupDirExists && settings.KeepBackups)
                AnsiConsole.MarkupLine($"  Backup directory: [yellow]kept[/] (--keep-backups)");

            var preserveState = settings.KeepBackups || settings.KeepState;
            if (stateDirExists && !preserveState)
                AnsiConsole.MarkupLine($"  State directory: [blue]{stateDir.EscapeMarkup()}[/]");
            else if (stateDirExists)
            {
                var reason = settings.KeepBackups ? "--keep-backups implies state preservation" : "--keep-state";
                AnsiConsole.MarkupLine($"  State directory: [yellow]kept[/] ({reason.EscapeMarkup()})");
            }

            if (binaryPath is not null)
                AnsiConsole.MarkupLine($"  Binary: [blue]{binaryPath.EscapeMarkup()}[/]");
            else
                AnsiConsole.MarkupLine("  Binary: [yellow]unknown path, skipping[/]");

            if (!configItemExists && !backupDirExists && !stateDirExists && systemdUnits.Count == 0 && binaryPath is null)
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

            var deleteState = false;
            if (stateDirExists && UninstallDataPolicy.ShouldDeleteState(settings.KeepBackups, settings.KeepState))
            {
                deleteState = settings.Yes || AnsiConsole.Confirm(
                    "Delete local application state and history? This cannot be undone.",
                    defaultValue: false);
            }

            if (deleteBackups
                && !deleteState
                && UninstallDataPolicy.IsSameOrNestedPath(stateDir, backupDir))
            {
                deleteBackups = false;
                AnsiConsole.MarkupLine(
                    "[yellow]Backup deletion skipped:[/] the preserved state directory is inside the backup directory.");
            }

            AnsiConsole.WriteLine();
            var removed = new List<string>();
            var skipped = new List<string>();

            // 1. Stop & disable systemd timers, remove unit files
            if (systemdUnits.Count > 0)
                await RemoveSystemdUnits(systemdUnits, removed, skipped);

            // 2. Remove config directory
            if (configItemExists)
            {
                try
                {
                    if (removeWholeConfigDirectory)
                    {
                        Directory.Delete(configDir, recursive: true);
                        removed.Add($"Config directory: {configDir}");
                        AnsiConsole.MarkupLine($"  [green]Removed[/] {configDir.EscapeMarkup()}");
                    }
                    else
                    {
                        File.Delete(configPath);
                        removed.Add($"Config file: {configPath}");
                        AnsiConsole.MarkupLine($"  [green]Removed[/] {configPath.EscapeMarkup()}");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    var configItem = removeWholeConfigDirectory ? configDir : configPath;
                    skipped.Add($"Configuration: {configItem} (permission denied)");
                    AnsiConsole.MarkupLine($"  [yellow]Skipped[/] {configItem.EscapeMarkup()} (permission denied)");
                    AnsiConsole.MarkupLine($"  Remove manually with sufficient permissions: [blue]{configItem.EscapeMarkup()}[/]");
                }
            }

            // 3. Remove state separately before a potentially enclosing backup tree.
            if (deleteState)
                DeleteDirectory(stateDir, "State directory", removed, skipped);
            else if (stateDirExists)
                skipped.Add($"State directory: {stateDir} (preserved)");

            // 4. Remove backup directory
            if (deleteBackups)
                DeleteDirectory(backupDir, "Backup directory", removed, skipped);
            else if (backupDirExists)
                skipped.Add($"Backup directory: {backupDir} (preserved)");

            // 5. Delete binary (last step — safe on Linux, process keeps running)
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

    private static void DeleteDirectory(
        string path,
        string label,
        List<string> removed,
        List<string> skipped)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
            removed.Add($"{label}: {path}");
            AnsiConsole.MarkupLine($"  [green]Removed[/] {path.EscapeMarkup()}");
        }
        catch (UnauthorizedAccessException)
        {
            skipped.Add($"{label}: {path} (permission denied)");
            AnsiConsole.MarkupLine($"  [yellow]Skipped[/] {path.EscapeMarkup()} (permission denied)");
            AnsiConsole.MarkupLine($"  Remove manually with sufficient permissions: [blue]{path.EscapeMarkup()}[/]");
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

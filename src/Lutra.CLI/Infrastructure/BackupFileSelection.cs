using Lutra.Core.Configuration;
using Lutra.Core.History;
using Spectre.Console;

namespace Lutra.CLI.Infrastructure;

/// <summary>
/// Shared helpers for the restore and verify commands: interactive target/backup
/// selection and backup file path resolution.
/// </summary>
internal static class BackupFileSelection
{
    /// <summary>
    /// Prompts the user to select a configured database target.
    /// Returns <see langword="null"/> when no targets are configured.
    /// </summary>
    public static DatabaseTarget? PromptForTarget(BackupConfig config)
    {
        if (config.Databases.Count == 0)
            return null;

        if (config.Databases.Count == 1)
            return config.Databases[0];

        return AnsiConsole.Prompt(
            new SelectionPrompt<DatabaseTarget>()
                .Title("Select a [green]target[/] to restore:")
                .PageSize(10)
                .UseConverter(t => $"{t.Name} ({t.Type}, database: {t.Database}, container: {t.Container})")
                .AddChoices(config.Databases));
    }

    /// <summary>
    /// Prompts the user to select a successful backup from the target's history.
    /// Returns <see langword="null"/> when no successful backups exist.
    /// </summary>
    public static async Task<BackupRecord?> PromptForBackupAsync(
        IBackupHistoryService historyService,
        DatabaseTarget target)
    {
        var records = await historyService.GetRecordsByTargetAsync(target.Name);
        var backups = records
            .Where(r => r.Success && r.RecordType is null)
            .Take(15)
            .ToList();

        if (backups.Count == 0)
            return null;

        return AnsiConsole.Prompt(
            new SelectionPrompt<BackupRecord>()
                .Title($"Select a [green]backup[/] to restore for [bold]{target.Name.EscapeMarkup()}[/]:")
                .PageSize(10)
                .UseConverter(r => $"{r.Timestamp:yyyy-MM-dd HH:mm:ss} UTC — {r.FileName} ({FormatBytes(r.FileSizeBytes)})")
                .AddChoices(backups));
    }

    /// <summary>
    /// Finds the most recent successful backup record whose file exists on disk.
    /// </summary>
    public static async Task<BackupRecord?> FindLatestBackupAsync(
        BackupConfig config,
        IBackupHistoryService historyService,
        DatabaseTarget target)
    {
        var records = await historyService.GetRecordsByTargetAsync(target.Name);

        return records
            .Where(r => r.Success && r.RecordType is null)
            .FirstOrDefault(r => File.Exists(GetBackupPath(config, target, r)));
    }

    /// <summary>
    /// Resolves a user-supplied backup file reference: either an existing path, or a
    /// file name inside the target's backup directory.
    /// </summary>
    public static string ResolveBackupFilePath(BackupConfig config, DatabaseTarget target, string file)
    {
        if (File.Exists(file))
            return Path.GetFullPath(file);

        return Path.Combine(config.BackupDirectory, target.Name, file);
    }

    /// <summary>
    /// Returns the full path of the backup file for a history record.
    /// </summary>
    public static string GetBackupPath(BackupConfig config, DatabaseTarget target, BackupRecord record)
        => Path.Combine(config.BackupDirectory, target.Name, record.FileName);

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {suffixes[order]}";
    }
}

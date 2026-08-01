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
    /// Prompts the user to select a configured target (database or file).
    /// Returns <see langword="null"/> when no targets are configured.
    /// </summary>
    public static IBackupTarget? PromptForTarget(BackupConfig config)
    {
        var targets = config.AllTargets().ToList();

        if (targets.Count == 0)
            return null;

        if (targets.Count == 1)
            return targets[0];

        return AnsiConsole.Prompt(
            new SelectionPrompt<IBackupTarget>()
                .Title("Select a [green]target[/]:")
                .PageSize(10)
                .UseConverter(DescribeTarget)
                .AddChoices(targets));
    }

    /// <summary>
    /// Prompts the user to select a successful backup from the target's history.
    /// Returns <see langword="null"/> when no successful backups exist.
    /// </summary>
    public static async Task<HistoryRecord?> PromptForBackupAsync(
        IBackupHistoryService historyService,
        IBackupTarget target)
    {
        var records = await historyService.GetRecordsByTargetAsync(target.Name);
        var backups = records
            .Where(r => r.Status == HistoryOperationStatus.Succeeded
                && r.OperationType == HistoryOperationType.Backup
                && !string.IsNullOrWhiteSpace(r.FileName))
            .Take(15)
            .ToList();

        if (backups.Count == 0)
            return null;

        return AnsiConsole.Prompt(
            new SelectionPrompt<HistoryRecord>()
                .Title($"Select a [green]backup[/] for [bold]{target.Name.EscapeMarkup()}[/]:")
                .PageSize(10)
                .UseConverter(r => $"{r.StartedAt:yyyy-MM-dd HH:mm:ss} UTC — {r.FileName} ({FormatBytes(r.FileSizeBytes ?? 0)})")
                .AddChoices(backups));
    }

    /// <summary>
    /// Finds the most recent successful backup record whose file exists on disk.
    /// </summary>
    public static async Task<HistoryRecord?> FindLatestBackupAsync(
        BackupConfig config,
        IBackupHistoryService historyService,
        IBackupTarget target)
    {
        var records = await historyService.GetRecordsByTargetAsync(target.Name);

        return records
            .Where(r => r.Status == HistoryOperationStatus.Succeeded
                && r.OperationType == HistoryOperationType.Backup
                && !string.IsNullOrWhiteSpace(r.FileName))
            .FirstOrDefault(r => File.Exists(GetBackupPath(config, target, r)));
    }

    /// <summary>
    /// Resolves a user-supplied backup file reference: either an existing path, or a
    /// file name inside the target's backup directory.
    /// </summary>
    public static string ResolveBackupFilePath(BackupConfig config, IBackupTarget target, string file)
    {
        if (File.Exists(file))
            return Path.GetFullPath(file);

        return Path.Combine(config.BackupDirectory, target.Name, file);
    }

    /// <summary>
    /// Returns the full path of the backup file for a history record.
    /// </summary>
    public static string GetBackupPath(BackupConfig config, IBackupTarget target, HistoryRecord record)
        => Path.Combine(config.BackupDirectory, target.Name, record.FileName!);

    public static string DescribeTarget(IBackupTarget target) => target switch
    {
        DatabaseTarget db => $"{db.Name} ({db.Type}, database: {db.Database}, container: {db.Container})",
        FileTarget files => $"{files.Name} (files, {files.Paths.Count} path(s))",
        _ => target.Name
    };

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

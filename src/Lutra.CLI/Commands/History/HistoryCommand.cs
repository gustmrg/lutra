using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Lutra.Core.History;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.History;

public sealed class HistoryCommand : AsyncCommand<TargetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TargetSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var historyService = ServiceFactory.CreateHistoryService(config);

            IReadOnlyList<BackupRecord> records;
            if (settings.Target is not null)
            {
                ServiceFactory.ResolveTarget(config, settings.Target);
                records = await historyService.GetRecordsByTargetAsync(settings.Target);
            }
            else
            {
                records = await historyService.GetAllRecordsAsync();
            }

            if (records.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No backup history found.[/]");
                return 0;
            }

            var table = new Table();
            table.AddColumn("Target");
            table.AddColumn("Timestamp");
            table.AddColumn("File");
            table.AddColumn("Size");
            table.AddColumn("Duration");
            table.AddColumn("Status");

            foreach (var record in records)
            {
                var status = record.Success
                    ? "[green]OK[/]"
                    : $"[red]FAILED[/] {record.ErrorMessage?.EscapeMarkup()}";

                var size = record.Success ? FormatBytes(record.FileSizeBytes) : "-";
                var duration = $"{record.DurationMs}ms";

                table.AddRow(
                    record.TargetName.EscapeMarkup(),
                    record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    record.FileName.EscapeMarkup(),
                    size,
                    duration,
                    status);
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static string FormatBytes(long bytes)
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

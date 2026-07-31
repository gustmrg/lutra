using System.Text.Json;
using System.Text.Json.Serialization;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupReconcileCommand : AsyncCommand<BackupReconcileSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BackupReconcileSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            if (settings.Target is not null)
                ServiceFactory.ResolveTarget(config, settings.Target);

            var service = ServiceFactory.CreateReconciliationService(config);
            var report = await service.ReconcileAsync(settings.Target);

            if (settings.Json)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
                };
                Console.WriteLine(JsonSerializer.Serialize(report, options));
                return report.IsClean ? 0 : 1;
            }

            if (report.IsClean)
            {
                AnsiConsole.MarkupLine($"[green]Reconciliation passed.[/] Checked {report.TargetsChecked} target(s); no inconsistencies found.");
                return 0;
            }

            var table = new Table();
            table.AddColumn("Target");
            table.AddColumn("Finding");
            table.AddColumn("Path");
            table.AddColumn("Details");
            foreach (var finding in report.Findings)
            {
                table.AddRow(
                    finding.TargetName.EscapeMarkup(),
                    FormatType(finding.Type),
                    finding.Path.EscapeMarkup(),
                    finding.Message.EscapeMarkup());
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[yellow]Found {report.Findings.Count} reconciliation issue(s). No files were changed.[/]");
            return 1;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static string FormatType(ReconciliationFindingType type) => type switch
    {
        ReconciliationFindingType.FileWithoutHistory => "file without history",
        ReconciliationFindingType.HistoryWithoutFile => "history without file",
        ReconciliationFindingType.MissingChecksum => "missing checksum",
        ReconciliationFindingType.MissingManifest => "missing manifest",
        _ => type.ToString()
    };
}

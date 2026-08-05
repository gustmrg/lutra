using System.Text.Json;
using System.Text.Json.Serialization;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Configuration;
using Lutra.Core.Health;
using Lutra.Core.History;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Health;

public sealed class HealthCommand : AsyncCommand<HealthSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, HealthSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var historyService = ServiceFactory.CreateHistoryService(config);
            var detector = ServiceFactory.CreateAnomalyDetector(config);
            var artifactChecker = ServiceFactory.CreateArtifactHealthChecker(config);

            var targets = settings.Target is not null
                ? [ServiceFactory.ResolveTarget(config, settings.Target)]
                : config.AllTargets().ToList();

            var reports = new List<HealthReport>();

            foreach (var target in targets)
            {
                var records = await historyService.GetRecordsByTargetAsync(target.Name);
                var backupRecords = records
                    .Where(r => r.OperationType == HistoryOperationType.Backup
                        && r.Status.IsTerminal())
                    .ToList();
                var report = detector.Analyze(backupRecords, target);
                report.Findings.AddRange(await artifactChecker.CheckAsync(target, backupRecords));
                reports.Add(report);
            }

            if (settings.Json)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
                };
                Console.WriteLine(JsonSerializer.Serialize(reports, options));
            }
            else
            {
                RenderReports(reports);
            }

            var worstStatus = reports.Max(r => r.OverallStatus);
            var healthy = worstStatus == OverallStatus.Healthy;
            await NotificationConsole.SendAsync(
                config,
                healthy ? "health_healthy" : "health_unhealthy",
                healthy,
                healthy
                    ? "All configured backup targets are healthy."
                    : $"Backup health is {worstStatus}; {reports.Sum(report => report.Findings.Count(finding => finding.Severity != Severity.Info))} actionable finding(s).",
                settings.Target);
            return worstStatus switch
            {
                OverallStatus.Critical => 2,
                OverallStatus.Warning => 1,
                _ => 0
            };
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static void RenderReports(List<HealthReport> reports)
    {
        foreach (var report in reports)
        {
            var statusMarkup = report.OverallStatus switch
            {
                OverallStatus.Healthy => "[green]Healthy[/]",
                OverallStatus.Warning => "[yellow]Warning[/]",
                OverallStatus.Critical => "[red]Critical[/]",
                _ => "[grey]Unknown[/]"
            };

            AnsiConsole.MarkupLine(
                $"\n[bold]{report.TargetName.EscapeMarkup()}[/]  {statusMarkup}  ({report.TotalBackupsAnalyzed} backups analyzed)");

            if (report.Findings.Count == 0)
            {
                AnsiConsole.MarkupLine("  [green]No issues detected.[/]");
                continue;
            }

            var table = new Table();
            table.Border(TableBorder.Simple);
            table.AddColumn("Severity");
            table.AddColumn("Type");
            table.AddColumn("Message");
            table.AddColumn("Detail");

            foreach (var finding in report.Findings.OrderByDescending(f => f.Severity))
            {
                var severity = finding.Severity switch
                {
                    Severity.Critical => "[red]CRITICAL[/]",
                    Severity.Warning => "[yellow]WARNING[/]",
                    Severity.Info => "[blue]INFO[/]",
                    _ => "[grey]?[/]"
                };

                table.AddRow(
                    severity,
                    finding.Type.ToString().EscapeMarkup(),
                    finding.Message.EscapeMarkup(),
                    (finding.Detail ?? "").EscapeMarkup());
            }

            AnsiConsole.Write(table);
        }
    }
}

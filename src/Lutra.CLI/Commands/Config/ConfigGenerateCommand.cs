using Lutra.Core.Compose;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigGenerateCommand : Command<ConfigGenerateSettings>
{
    public override int Execute(CommandContext context, ConfigGenerateSettings settings)
    {
        try
        {
            var composePath = ResolveComposePath(settings);
            AnsiConsole.MarkupLine($"[blue]Parsing:[/] {composePath.EscapeMarkup()}");

            var composeFile = ComposeParser.Parse(composePath);
            var detected = DatabaseDetector.Detect(composeFile);

            ReportSkippedServices(composeFile, detected);

            if (detected.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No database services detected in compose file.[/]");
                return 0;
            }

            RenderDetectedDatabases(detected);

            if (settings.Interactive)
                detected = RunInteractivePrompts(detected);

            var backupDir = ConfigTemplates.GetDefaultBackupDirectory();
            var stateDir = ConfigTemplates.GetDefaultStateDirectory();
            var yamlContent = ConfigGenerator.Generate(detected, backupDir, stateDir);
            var envContent = ConfigGenerator.GenerateEnvTemplate(detected);

            var outputPath = settings.Output ?? Path.Combine(ConfigTemplates.GetDefaultConfigDirectory(), "lutra.yaml");
            var envPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", ".env");

            WriteOutput(outputPath, yamlContent, settings.Force);

            if (detected.Any(d => d.PasswordEnvVar is not null))
                WriteOutput(envPath, envContent, settings.Force);

            AnsiConsole.MarkupLine($"\n[green]Generated config:[/] {outputPath.EscapeMarkup()}");
            AnsiConsole.MarkupLine("[yellow]Review the generated files and update passwords before running backups.[/]");
            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    private static string ResolveComposePath(ConfigGenerateSettings settings)
    {
        if (settings.FromCompose is not null)
        {
            if (!File.Exists(settings.FromCompose))
                throw new FileNotFoundException($"Compose file not found: {settings.FromCompose}");
            return settings.FromCompose;
        }

        var found = ComposeParser.FindComposeFile(Directory.GetCurrentDirectory());
        if (found is null)
            throw new FileNotFoundException(
                "No docker-compose file found in current directory. Use --from-compose to specify the path.");

        return found;
    }

    private static void ReportSkippedServices(ComposeFile composeFile, List<DetectedDatabase> detected)
    {
        var detectedNames = detected.Select(d => d.ServiceName).ToHashSet();

        foreach (var service in composeFile.Services)
        {
            if (detectedNames.Contains(service.ServiceName))
                continue;

            if (service.UsesBuild && service.Image is null)
                AnsiConsole.MarkupLine(
                    $"[grey]Skipped '{service.ServiceName.EscapeMarkup()}': uses build context (no image specified)[/]");
        }
    }

    private static void RenderDetectedDatabases(List<DetectedDatabase> detected)
    {
        AnsiConsole.MarkupLine($"\n[bold]Detected {detected.Count} database service(s):[/]");

        var table = new Table();
        table.AddColumn("Service");
        table.AddColumn("Type");
        table.AddColumn("Container");
        table.AddColumn("Database");
        table.AddColumn("Confidence");

        foreach (var db in detected)
        {
            var confidence = db.Confidence switch
            {
                DetectionConfidence.High => "[green]High[/]",
                DetectionConfidence.Medium => "[yellow]Medium[/]",
                DetectionConfidence.Low => "[red]Low[/]",
                _ => "[grey]?[/]"
            };

            table.AddRow(
                db.ServiceName.EscapeMarkup(),
                db.Type.ToString(),
                db.ContainerName.EscapeMarkup(),
                (db.DatabaseName ?? "[yellow]unknown[/]").EscapeMarkup(),
                confidence);
        }

        AnsiConsole.Write(table);
    }

    private static List<DetectedDatabase> RunInteractivePrompts(List<DetectedDatabase> detected)
    {
        var result = new List<DetectedDatabase>();

        foreach (var db in detected)
        {
            if (db.Confidence == DetectionConfidence.Low)
            {
                var include = AnsiConsole.Confirm(
                    $"Include [yellow]{db.ServiceName.EscapeMarkup()}[/] ({db.ImageName?.EscapeMarkup()})? (Low confidence)");
                if (!include)
                    continue;
            }

            var dbName = db.DatabaseName;
            if (dbName is null)
            {
                dbName = AnsiConsole.Ask<string>(
                    $"Database name for [bold]{db.ServiceName.EscapeMarkup()}[/] ({db.Type}):");
            }

            result.Add(new DetectedDatabase
            {
                ServiceName = db.ServiceName,
                Type = db.Type,
                ContainerName = db.ContainerName,
                DatabaseName = dbName,
                Username = db.Username,
                PasswordEnvVar = db.PasswordEnvVar,
                ImageName = db.ImageName,
                Confidence = db.Confidence
            });
        }

        return result;
    }

    private static void WriteOutput(string path, string content, bool force)
    {
        if (File.Exists(path) && !force)
        {
            var overwrite = AnsiConsole.Confirm($"[yellow]{path.EscapeMarkup()}[/] already exists. Overwrite?", defaultValue: false);
            if (!overwrite)
            {
                AnsiConsole.MarkupLine($"[grey]Skipped writing {path.EscapeMarkup()}[/]");
                return;
            }
        }

        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, content);
    }
}

using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigInitCommand : AsyncCommand<ConfigInitSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ConfigInitSettings settings)
    {
        try
        {
            var configPath = ConfigFileHelper.ResolveConfigPath(settings.ConfigPath);
            var envFilePath = ConfigFileHelper.ResolveEnvPath(settings.EnvFilePath);
            var backupDirectory = ConfigTemplates.GetDefaultBackupDirectory();

            var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            var envDir = Path.GetDirectoryName(Path.GetFullPath(envFilePath))!;

            // Create directories
            ConfigFileHelper.CreateDirectoryIfNeeded(configDir);
            if (envDir != configDir)
                ConfigFileHelper.CreateDirectoryIfNeeded(envDir);
            ConfigFileHelper.CreateDirectoryIfNeeded(backupDirectory);

            // Write template files
            var configWritten = ConfigFileHelper.WriteFile(
                configPath,
                ConfigTemplates.GenerateYamlTemplate(backupDirectory),
                settings.Force);

            var envWritten = ConfigFileHelper.WriteFile(
                envFilePath,
                ConfigTemplates.GenerateEnvTemplate(),
                settings.Force);

            // Set .env permissions to 600 on Linux
            if (envWritten)
                ConfigFileHelper.SetEnvFilePermissions(envFilePath);

            // Print next steps
            AnsiConsole.WriteLine();
            PrintNextSteps(configPath, envFilePath);

            return Task.FromResult(0);
        }
        catch (UnauthorizedAccessException ex)
        {
            AnsiConsole.MarkupLine($"[red]Permission denied:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine("Try running with [blue]sudo[/] for system-wide installation.");
            return Task.FromResult(1);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return Task.FromResult(1);
        }
    }

    private static void PrintNextSteps(string configPath, string envFilePath)
    {
        AnsiConsole.MarkupLine("[bold]Next steps:[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("1. [bold]Edit configuration:[/]");
        AnsiConsole.MarkupLine($"   [blue]nano {configPath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            "   Update container names, database names, and remove examples you don't need.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("2. [bold]Set credentials:[/]");
        AnsiConsole.MarkupLine($"   [blue]nano {envFilePath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine("   Replace placeholder passwords with real values.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("3. [bold]Validate configuration:[/]");
        AnsiConsole.MarkupLine(
            $"   [blue]lutra config validate --config {configPath.EscapeMarkup()} --env-file {envFilePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("4. [bold]Run your first backup:[/]");
        AnsiConsole.MarkupLine(
            $"   [blue]lutra backup run --config {configPath.EscapeMarkup()} --env-file {envFilePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
    }
}

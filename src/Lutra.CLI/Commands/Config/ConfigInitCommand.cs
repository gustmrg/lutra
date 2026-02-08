using System.Runtime.InteropServices;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigInitCommand : AsyncCommand<ConfigInitSettings>
{
    private const string DefaultSystemConfigPath = "/etc/lutra/lutra.yaml";
    private const string DefaultSystemEnvPath = "/etc/lutra/.env";

    public override Task<int> ExecuteAsync(CommandContext context, ConfigInitSettings settings)
    {
        try
        {
            var configPath = ResolveConfigPath(settings.ConfigPath);
            var envFilePath = ResolveEnvPath(settings.EnvFilePath);
            var backupDirectory = ConfigTemplates.GetDefaultBackupDirectory();

            var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            var envDir = Path.GetDirectoryName(Path.GetFullPath(envFilePath))!;

            // Create directories
            CreateDirectoryIfNeeded(configDir);
            if (envDir != configDir)
                CreateDirectoryIfNeeded(envDir);
            CreateDirectoryIfNeeded(backupDirectory);

            // Write template files
            var configWritten = WriteFileIfNeeded(
                configPath,
                ConfigTemplates.GenerateYamlTemplate(backupDirectory),
                settings.Force);

            var envWritten = WriteFileIfNeeded(
                envFilePath,
                ConfigTemplates.GenerateEnvTemplate(),
                settings.Force);

            // Set .env permissions to 600 on Linux
            if (envWritten && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                File.SetUnixFileMode(envFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                AnsiConsole.MarkupLine($"  Set permissions [blue]600[/] on {envFilePath.EscapeMarkup()}");
            }

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

    private static string ResolveConfigPath(string configPath)
    {
        if (!Environment.IsPrivilegedProcess && configPath == DefaultSystemConfigPath)
            return Path.Combine(ConfigTemplates.GetDefaultConfigDirectory(), "lutra.yaml");

        return configPath;
    }

    private static string ResolveEnvPath(string envFilePath)
    {
        if (!Environment.IsPrivilegedProcess && envFilePath == DefaultSystemEnvPath)
            return Path.Combine(ConfigTemplates.GetDefaultConfigDirectory(), ".env");

        return envFilePath;
    }

    private static void CreateDirectoryIfNeeded(string path)
    {
        if (Directory.Exists(path))
            return;

        Directory.CreateDirectory(path);
        AnsiConsole.MarkupLine($"  [green]Created[/] directory: {path.EscapeMarkup()}");
    }

    private static bool WriteFileIfNeeded(string path, string content, bool force)
    {
        var exists = File.Exists(path);

        if (exists && !force)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]Skipped[/] {path.EscapeMarkup()} (already exists, use --force to overwrite)");
            return false;
        }

        File.WriteAllText(path, content);

        var verb = exists ? "Overwritten" : "Created";
        AnsiConsole.MarkupLine($"  [green]{verb}[/] {path.EscapeMarkup()}");
        return true;
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
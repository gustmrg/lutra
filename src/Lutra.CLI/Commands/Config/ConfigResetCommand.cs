using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigResetCommand : AsyncCommand<GlobalSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        try
        {
            var configPath = ConfigFileHelper.ResolveConfigPath(settings.ConfigPath);
            var envFilePath = ConfigFileHelper.ResolveEnvPath(settings.EnvFilePath);

            var configExists = File.Exists(configPath);
            var envExists = File.Exists(envFilePath);

            if (!configExists && !envExists)
            {
                AnsiConsole.MarkupLine("[yellow]No configuration files found.[/]");
                AnsiConsole.MarkupLine("Run [blue]lutra config init[/] to create them.");
                return Task.FromResult(1);
            }

            // Show what will be overwritten
            AnsiConsole.MarkupLine("[bold]The following files will be reset to defaults:[/]");
            if (configExists)
                AnsiConsole.MarkupLine($"  {configPath.EscapeMarkup()}");
            if (envExists)
                AnsiConsole.MarkupLine($"  {envFilePath.EscapeMarkup()}");
            AnsiConsole.WriteLine();

            if (!AnsiConsole.Confirm("This will overwrite your current configuration. Continue?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/]");
                return Task.FromResult(0);
            }

            var backupDirectory = ConfigTemplates.GetDefaultBackupDirectory();

            if (configExists)
            {
                ConfigFileHelper.WriteFile(
                    configPath,
                    ConfigTemplates.GenerateYamlTemplate(backupDirectory),
                    overwrite: true);
            }

            if (envExists)
            {
                ConfigFileHelper.WriteFile(
                    envFilePath,
                    ConfigTemplates.GenerateEnvTemplate(),
                    overwrite: true);

                ConfigFileHelper.SetEnvFilePermissions(envFilePath);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Next steps:[/]");
            AnsiConsole.MarkupLine($"  Edit [blue]{configPath.EscapeMarkup()}[/] and [blue]{envFilePath.EscapeMarkup()}[/] with your settings.");
            AnsiConsole.MarkupLine($"  Then run [blue]lutra config validate[/] to verify.");

            return Task.FromResult(0);
        }
        catch (UnauthorizedAccessException ex)
        {
            AnsiConsole.MarkupLine($"[red]Permission denied:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine("Try running with [blue]sudo[/] for system-wide configuration.");
            return Task.FromResult(1);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return Task.FromResult(1);
        }
    }
}

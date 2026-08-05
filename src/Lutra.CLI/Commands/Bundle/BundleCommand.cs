using Lutra.CLI.Commands.Config;
using Lutra.CLI.Infrastructure;
using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Bundle;

public sealed class BundleCommand : AsyncCommand<BundleSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BundleSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var (configPath, envPath) = ConfigFileHelper.ResolvePaths(
                settings.ConfigPath,
                settings.EnvFilePath);
            var result = await AnsiConsole.Status().StartAsync("Building disaster recovery bundle...", _ =>
                ServiceFactory.CreateBundleService(config).CreateAsync(
                    configPath, envPath, settings.Output, settings.Encrypt));

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Bundle failed:[/] {result.ErrorMessage?.EscapeMarkup() ?? "Unknown error"}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Recovery bundle created:[/] {result.FilePath!.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"  Targets: {result.TargetCount}");
            AnsiConsole.MarkupLine($"  Checksum: {BackupIntegrity.GetChecksumPath(result.FilePath!).EscapeMarkup()}");
            return 0;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }
}

using Lutra.CLI.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Recovery;

public sealed class EnvironmentInspectCommand : AsyncCommand<EnvironmentArtifactSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EnvironmentArtifactSettings settings)
    {
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            AnsiConsole.MarkupLine("[yellow]Warning:[/] environment recovery sets are plaintext; checksums do not authenticate the sender.");
            var result = await ServiceFactory.CreateEnvironmentRestoreService(config).InspectAsync(settings.File);
            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Inspection failed:[/] {result.ErrorMessage!.EscapeMarkup()}");
                return 1;
            }

            var manifest = result.Manifest!;
            AnsiConsole.MarkupLine($"[green]Recovery set is valid.[/] Format version: [blue]{manifest.FormatVersion}[/]");
            AnsiConsole.MarkupLine($"  Artifact: {result.Descriptor!.ArtifactFileName.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"  Created: {manifest.CreatedAt:O}");
            AnsiConsole.MarkupLine("  Transport checksum: [green]valid[/]");
            AnsiConsole.MarkupLine($"  SHA-256: {result.Descriptor.Sha256[..12]}");
            AnsiConsole.MarkupLine($"  Sources: {manifest.Sources.Count}");
            AnsiConsole.MarkupLine($"  Inventory sections: {result.Inventory!.Sections.Count}");
            foreach (var section in result.Inventory.Sections)
                AnsiConsole.MarkupLine($"    - {section.Name.EscapeMarkup()}: {section.Status}");
            foreach (var source in manifest.Sources.OrderBy(source => source.RestoreOrder))
                AnsiConsole.MarkupLine($"    {source.RestoreOrder + 1}. {source.Name.EscapeMarkup()} ({source.Kind})");
            return 0;
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]Inspection failed during initialization.[/]");
            return 1;
        }
    }
}

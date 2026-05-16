using Lutra.Core.Backup;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupVerifyFileCommand : AsyncCommand<BackupVerifyFileSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BackupVerifyFileSettings settings)
    {
        var filePath = settings.FilePath!;
        var result = await BackupIntegrity.VerifyFileAsync(filePath);

        if (!result.Success)
        {
            AnsiConsole.MarkupLine($"[red]Verification failed:[/] {result.Message.EscapeMarkup()}");
            if (result.ExpectedSha256 is not null)
                AnsiConsole.MarkupLine($"  Expected: [blue]{result.ExpectedSha256}[/]");
            if (result.ActualSha256 is not null)
                AnsiConsole.MarkupLine($"  Actual:   [blue]{result.ActualSha256}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]Backup file verified.[/]");
        AnsiConsole.MarkupLine($"  File:     [blue]{filePath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"  SHA-256:  [blue]{result.ActualSha256}[/]");
        AnsiConsole.MarkupLine($"  Manifest: [blue]{result.ManifestPath?.EscapeMarkup()}[/]");
        return 0;
    }
}

using Lutra.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands;

/// <summary>
/// Base for every command: runs it and turns the failures any of them can hit
/// into one consistent message and exit code.
/// </summary>
public abstract class LutraCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
    {
        try
        {
            return await RunAsync(settings);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (ConfigurationException ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            AnsiConsole.MarkupLine($"[red]Permission denied:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine("Try running with [blue]sudo[/] if this command writes to system directories.");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }
    }

    protected abstract Task<int> RunAsync(TSettings settings);

    /// <summary>Reports that a prompt is needed but the terminal cannot show one.</summary>
    /// <param name="alternative">How to run the command without the prompt.</param>
    protected static bool EnsureInteractive(string alternative)
    {
        if (AnsiConsole.Profile.Capabilities.Interactive)
            return true;

        AnsiConsole.MarkupLine($"[red]This command requires an interactive terminal.[/] {alternative.EscapeMarkup()}");
        return false;
    }
}

using Lutra.CLI.Infrastructure;
using Lutra.Core.Recovery;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Recovery;

public sealed class EnvironmentRestoreCommand : AsyncCommand<EnvironmentRestoreSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EnvironmentRestoreSettings settings)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var config = ServiceFactory.LoadConfig(settings);
            var service = ServiceFactory.CreateEnvironmentRestoreService(config);
            var dryRunOptions = Options(settings, apply: false);
            var preflight = await service.RestoreAsync(settings.File, dryRunOptions, cancellation.Token);
            if (!preflight.Success)
            {
                AnsiConsole.MarkupLine($"[red]Preflight failed:[/] {preflight.ErrorMessage!.EscapeMarkup()}");
                return 1;
            }

            PrintPlan(preflight.Plan!);
            if (!settings.Apply)
            {
                AnsiConsole.MarkupLine("[green]Dry run completed. No changes were made.[/]");
                return 0;
            }
            if (!preflight.Plan!.CanApply)
            {
                AnsiConsole.MarkupLine("[red]The recovery plan cannot be applied.[/]");
                return 1;
            }
            if (!Confirm(settings.Yes))
                return 1;

            var result = await service.RestoreAsync(
                settings.File,
                Options(settings, apply: true) with
                {
                    ExpectedPlanToken = preflight.Plan.ConfirmationToken
                },
                cancellation.Token);
            if (!result.Success)
            {
                var resume = result.ResumeReportPath is null
                    ? ""
                    : $" Resume report: {result.ResumeReportPath.EscapeMarkup()}";
                AnsiConsole.MarkupLine($"[red]Restore failed:[/] {result.ErrorMessage!.EscapeMarkup()}{resume}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Environment restore completed[/] in {result.Duration.TotalSeconds:0.0}s.");
            if (result.RollbackDirectory is not null)
                AnsiConsole.MarkupLine($"  Rollback copies: {result.RollbackDirectory.EscapeMarkup()}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Environment restore cancelled.[/]");
            return 1;
        }
        catch
        {
            AnsiConsole.MarkupLine("[red]Environment restore failed during initialization.[/]");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static EnvironmentRestoreOptions Options(EnvironmentRestoreSettings settings, bool apply)
        => new(
            settings.Root,
            Apply: apply,
            IncludeVolumes: settings.IncludeVolumes,
            ActivateServices: settings.ActivateServices,
            CreateRollbackCopy: !settings.NoRollbackCopy);

    private static void PrintPlan(EnvironmentRestorePlan plan)
    {
        AnsiConsole.Write(new Panel(
                $"Artifact: [bold]{Path.GetFileName(plan.ArtifactPath).EscapeMarkup()}[/]\n" +
                $"Root: [bold]{plan.RootPath.EscapeMarkup()}[/]\n" +
                $"Actions: [bold]{plan.Actions.Count}[/]\n" +
                $"Staging space: [bold]{plan.StagingRequiredBytes:N0} / {plan.StagingAvailableBytes:N0} bytes[/]\n" +
                $"Destination space: [bold]{plan.DestinationRequiredBytes:N0} / {plan.DestinationAvailableBytes:N0} bytes[/]")
            .Header("[yellow] ENVIRONMENT RECOVERY PLAN [/]")
            .Border(BoxBorder.Heavy));
        AnsiConsole.MarkupLine("[yellow]Plaintext artifact; checksum integrity does not authenticate its sender.[/]");
        foreach (var action in plan.Actions)
            AnsiConsole.MarkupLine(
                $"  {action.Order + 1}. {action.GetType().Name.Replace("Environment", "", StringComparison.Ordinal).Replace("RestoreAction", "", StringComparison.Ordinal)} " +
                $"{action.Destination.EscapeMarkup()} [{action.State.ToString().ToLowerInvariant()}]");
        foreach (var warning in plan.Warnings)
            AnsiConsole.MarkupLine($"  [yellow]Warning:[/] {warning.EscapeMarkup()}");
        if (plan.MissingTools.Count > 0)
            AnsiConsole.MarkupLine($"  [red]Missing tools:[/] {string.Join(", ", plan.MissingTools).EscapeMarkup()}");
    }

    private static bool Confirm(bool yes)
    {
        if (yes)
            return true;
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]Applying requires an interactive confirmation or --yes.[/]");
            return false;
        }
        var confirmed = AnsiConsole.Prompt(
            new ConfirmationPrompt("Apply this environment recovery plan?") { DefaultValue = false });
        if (!confirmed)
            AnsiConsole.MarkupLine("[yellow]Restore cancelled. No changes were made.[/]");
        return confirmed;
    }
}

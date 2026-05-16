using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Cleanup;

public sealed class CleanupSettings : TargetSettings
{
    [CommandOption("--dry-run")]
    [Description("Preview retention cleanup without deleting files or history records.")]
    public bool DryRun { get; set; }
}

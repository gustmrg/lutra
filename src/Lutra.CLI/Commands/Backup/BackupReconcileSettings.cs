using System.ComponentModel;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupReconcileSettings : TargetSettings
{
    [Spectre.Console.Cli.CommandOption("--json")]
    [Description("Write the reconciliation report as JSON.")]
    public bool Json { get; set; }
}

using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Cleanup;

public sealed class CleanupSettings : TargetSettings
{
    [CommandOption("--dry-run")]
    [Description("Preview retention cleanup without deleting files or history records.")]
    public bool DryRun { get; set; }

    [CommandOption("--orphan-sidecars")]
    [Description("Remove checksum/manifest sidecars whose backup file is missing.")]
    public bool OrphanSidecars { get; set; }

    [CommandOption("--orphan-files")]
    [Description("Remove backup files not tracked by successful history (requires confirmation or --force).")]
    public bool OrphanFiles { get; set; }

    [CommandOption("--prune-history")]
    [Description("Prune old failures and verify/sync records using max_age_days.")]
    public bool PruneHistory { get; set; }

    [CommandOption("--force")]
    [Description("Confirm deletion of untracked backup files non-interactively.")]
    public bool Force { get; set; }
}

using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Sync;

public sealed class SyncSettings : TargetSettings
{
    [CommandOption("--dry-run")]
    [Description("Show what rsync would transfer without changing the destination.")]
    public bool DryRun { get; set; }

    [CommandOption("--delete")]
    [Description("Delete remote files that no longer exist locally; remote deletion is opt-in.")]
    public bool Delete { get; set; }

    [CommandOption("--validate")]
    [Description("Validate SSH, remote destination access, and local/remote rsync.")]
    public bool ValidateOnly { get; set; }
}

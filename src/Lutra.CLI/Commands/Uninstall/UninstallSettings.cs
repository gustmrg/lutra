using System.ComponentModel;
using Lutra.CLI.Commands;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Uninstall;

public class UninstallSettings : GlobalSettings
{
    [CommandOption("--yes|-y")]
    [Description("Skip prompts; deletes backup and state data unless their preservation flags are set.")]
    [DefaultValue(false)]
    public bool Yes { get; set; }

    [CommandOption("--keep-backups")]
    [Description("Preserve the backup data directory during uninstall.")]
    [DefaultValue(false)]
    public bool KeepBackups { get; set; }

    [CommandOption("--keep-state")]
    [Description("Preserve the local application state directory during uninstall.")]
    [DefaultValue(false)]
    public bool KeepState { get; set; }
}

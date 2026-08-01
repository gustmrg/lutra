using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Uninstall;

public class UninstallSettings : CommandSettings
{
    [CommandOption("--yes|-y")]
    [Description("Skip all confirmation prompts; also permits backup deletion unless --keep-backups is set.")]
    [DefaultValue(false)]
    public bool Yes { get; set; }

    [CommandOption("--keep-backups")]
    [Description("Preserve the backup data directory during uninstall.")]
    [DefaultValue(false)]
    public bool KeepBackups { get; set; }
}

using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Uninstall;

public class UninstallSettings : CommandSettings
{
    [CommandOption("--yes|-y")]
    [Description("Skip confirmation prompts.")]
    [DefaultValue(false)]
    public bool Yes { get; set; }

    [CommandOption("--keep-backups")]
    [Description("Preserve backup data directory.")]
    [DefaultValue(false)]
    public bool KeepBackups { get; set; }
}

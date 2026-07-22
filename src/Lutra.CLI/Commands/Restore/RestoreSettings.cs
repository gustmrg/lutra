using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Restore;

public class RestoreSettings : GlobalSettings
{
    [CommandOption("--target <NAME>")]
    [Description("Database target name to restore. If omitted, you will be prompted.")]
    public string? Target { get; set; }

    [CommandOption("--file <PATH>")]
    [Description("Backup file to restore. Accepts a full path or a file name inside the target's backup directory. If omitted, you will be prompted.")]
    public string? File { get; set; }

    [CommandOption("--force")]
    [Description("Skip the confirmation prompt (for automation).")]
    [DefaultValue(false)]
    public bool Force { get; set; }
}

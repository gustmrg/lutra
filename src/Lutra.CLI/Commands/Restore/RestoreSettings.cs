using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Restore;

public class RestoreSettings : GlobalSettings
{
    [CommandOption("--target <NAME>")]
    [Description("Target name to restore (database, file, or volume). If omitted, you will be prompted.")]
    public string? Target { get; set; }

    [CommandOption("--file <PATH>")]
    [Description("Backup artifact to restore. Accepts a full path or a file name inside the target's backup directory. If omitted, you will be prompted.")]
    public string? File { get; set; }

    [CommandOption("--force")]
    [Description("Skip the confirmation prompt (for automation).")]
    [DefaultValue(false)]
    public bool Force { get; set; }

    [CommandOption("--destination <DIR>")]
    [Description("File targets only: directory to extract the archive into. Defaults to / (original locations).")]
    public string? Destination { get; set; }

    [CommandOption("--chain <PATH>")]
    [Description("SQL Server only: one file in an ordered restore chain. Repeat once per file, in restore order.")]
    public string[] Chain { get; set; } = [];
}

using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Verify;

public class VerifySettings : GlobalSettings
{
    [CommandOption("--target <NAME>")]
    [Description("Database target name to verify. If omitted, you will be prompted.")]
    public string? Target { get; set; }

    [CommandOption("--file <PATH>")]
    [Description("Backup file to verify. Accepts a full path or a file name inside the target's backup directory. Defaults to the latest successful backup.")]
    public string? File { get; set; }
}

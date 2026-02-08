using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands;

/// <summary>
/// Settings for commands that optionally operate on a specific database target.
/// </summary>
public class TargetSettings : GlobalSettings
{
    [CommandOption("--target <NAME>")]
    [Description("Specific database target name. If omitted, applies to all targets.")]
    public string? Target { get; set; }
}

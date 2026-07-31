using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Health;

public class HealthSettings : TargetSettings
{
    [CommandOption("--json")]
    [Description("Write health reports as JSON.")]
    public bool Json { get; set; }
}

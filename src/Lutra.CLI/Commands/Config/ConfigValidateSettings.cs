using System.ComponentModel;
using Lutra.CLI.Commands;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigValidateSettings : GlobalSettings
{
    [CommandOption("--preflight")]
    [Description("Also check local tools, systemd calendar expressions, Docker, containers, and dump tools.")]
    public bool Preflight { get; set; }
}

using System.ComponentModel;
using Lutra.CLI.Commands;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public sealed class ConfigValidateSettings : GlobalSettings
{
    [CommandOption("--preflight")]
    [Description("Also check systemd schedules, Docker, containers, dump tools, and offsite sync.")]
    public bool Preflight { get; set; }
}

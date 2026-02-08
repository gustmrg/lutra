using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public class ConfigInitSettings : GlobalSettings
{
    [CommandOption("--force")]
    [Description("Overwrite existing configuration files.")]
    [DefaultValue(false)]
    public bool Force { get; set; }
}
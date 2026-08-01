using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public class ConfigInitSettings : GlobalSettings
{
    [CommandOption("--force")]
    [Description("Overwrite existing configuration and environment files.")]
    [DefaultValue(false)]
    public bool Force { get; set; }
}

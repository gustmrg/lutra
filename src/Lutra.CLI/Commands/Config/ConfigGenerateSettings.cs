using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public class ConfigGenerateSettings : GlobalSettings
{
    [CommandOption("--from-compose <PATH>")]
    [Description("Path to docker-compose file. If omitted, searches the current directory.")]
    public string? FromCompose { get; set; }

    [CommandOption("--output <PATH>")]
    [Description("Output path for the generated lutra.yaml. If omitted, writes to the default config path.")]
    public string? Output { get; set; }

    [CommandOption("--interactive")]
    [Description("Prompt for missing values and confirmation of low-confidence detections.")]
    public bool Interactive { get; set; }

    [CommandOption("--force")]
    [Description("Overwrite existing config file without confirmation.")]
    public bool Force { get; set; }
}

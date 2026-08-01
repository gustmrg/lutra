using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Config;

public class ConfigGenerateSettings : GlobalSettings
{
    [CommandOption("--from-compose <PATH>")]
    [Description("Path to a Docker Compose file. If omitted, searches the current directory.")]
    public string? FromCompose { get; set; }

    [CommandOption("--output <PATH>")]
    [Description("Output path for generated lutra.yaml; .env is written beside it when needed. Defaults to the installation config path.")]
    public string? Output { get; set; }

    [CommandOption("--interactive")]
    [Description("Prompt for missing database values and low-confidence detections.")]
    public bool Interactive { get; set; }

    [CommandOption("--force")]
    [Description("Overwrite existing generated files without confirmation.")]
    public bool Force { get; set; }
}

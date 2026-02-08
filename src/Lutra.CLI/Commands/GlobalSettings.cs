using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands;

/// <summary>
/// Base settings shared by all CLI commands.
/// </summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    [Description("Path to the YAML configuration file.")]
    [DefaultValue("/etc/lutra/lutra.yaml")]
    public string ConfigPath { get; set; } = "/etc/lutra/lutra.yaml";

    [CommandOption("--env-file <PATH>")]
    [Description("Path to the .env file for credential resolution.")]
    [DefaultValue("/etc/lutra/.env")]
    public string EnvFilePath { get; set; } = "/etc/lutra/.env";
}

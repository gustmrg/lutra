using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands;

/// <summary>
/// Base settings shared by all CLI commands.
/// </summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    [Description("Path to the YAML configuration file. Defaults to the existing installation config.")]
    public string? ConfigPath { get; set; }

    [CommandOption("--env-file <PATH>")]
    [Description("Path to the .env file for credential resolution. Defaults to the existing installation config.")]
    public string? EnvFilePath { get; set; }
}

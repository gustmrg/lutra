using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Bundle;

public sealed class BundleSettings : GlobalSettings
{
    [CommandOption("--output <PATH>")]
    [Description("Output archive path. Defaults to backup_directory/bundles/.")]
    public string? Output { get; set; }

    [CommandOption("--encrypt")]
    [Description("Encrypt the completed bundle with the globally configured age recipient.")]
    public bool Encrypt { get; set; }
}

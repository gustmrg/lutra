using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Recovery;

public class EnvironmentArtifactSettings : GlobalSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("Path to an environment recovery artifact.")]
    public required string File { get; set; }
}

public sealed class EnvironmentRestoreSettings : EnvironmentArtifactSettings
{
    [CommandOption("--root <PATH>")]
    [Description("Filesystem root beneath which file payloads are restored.")]
    [DefaultValue("/")]
    public string Root { get; set; } = "/";

    [CommandOption("--apply")]
    [Description("Apply the preflight plan. Without this option, no changes are made.")]
    public bool Apply { get; set; }

    [CommandOption("--yes")]
    [Description("Confirm application non-interactively.")]
    public bool Yes { get; set; }

    [CommandOption("--include-volumes")]
    [Description("Destructively restore declared Docker volume payloads.")]
    public bool IncludeVolumes { get; set; }

    [CommandOption("--activate-services")]
    [Description("Validate and activate declared systemd units and Docker containers after restore.")]
    public bool ActivateServices { get; set; }

    [CommandOption("--no-rollback-copy")]
    [Description("Do not copy changed existing files into the rollback directory.")]
    public bool NoRollbackCopy { get; set; }
}

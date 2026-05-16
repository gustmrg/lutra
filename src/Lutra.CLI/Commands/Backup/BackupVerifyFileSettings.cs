using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Backup;

public sealed class BackupVerifyFileSettings : CommandSettings
{
    [CommandOption("--file <PATH>")]
    [Description("Path to the backup file to verify.")]
    public string? FilePath { get; set; }

    public override ValidationResult Validate()
    {
        return string.IsNullOrWhiteSpace(FilePath)
            ? ValidationResult.Error("--file is required.")
            : ValidationResult.Success();
    }
}

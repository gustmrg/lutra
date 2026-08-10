using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleInstallSettings : TargetSettings
{
    [CommandOption("--run-as <USER>")]
    [Description("Non-root OS user that will run every generated Lutra service.")]
    public string? RunAsUser { get; set; }
}

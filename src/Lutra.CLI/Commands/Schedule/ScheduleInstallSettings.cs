using System.ComponentModel;
using Spectre.Console.Cli;

namespace Lutra.CLI.Commands.Schedule;

public sealed class ScheduleInstallSettings : TargetSettings
{
    [CommandOption("--user <NAME>")]
    [Description("OS account the timers run as. Defaults to the account that invoked sudo.")]
    public string? User { get; set; }
}

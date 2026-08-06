namespace Lutra.Core.Recovery;

public sealed record EnvironmentScheduleUnitContent(string Service, string Timer);

public static class EnvironmentScheduleUnits
{
    public const string UnitName = "lutra-environment-backup";

    public static EnvironmentScheduleUnitContent Build(
        string lutraPath,
        string configPath,
        string envFilePath,
        string schedule)
    {
        var service = $"""
            [Unit]
            Description=Lutra environment recovery backup

            [Service]
            Type=oneshot
            ExecStart={Quote(lutraPath)} environment backup --config {Quote(configPath)} --env-file {Quote(envFilePath)}
            """;
        var timer = $"""
            [Unit]
            Description=Lutra environment recovery backup timer

            [Timer]
            OnCalendar={schedule}
            Persistent=true

            [Install]
            WantedBy=timers.target
            """;
        return new EnvironmentScheduleUnitContent(service, timer);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

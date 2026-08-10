namespace Lutra.CLI.Commands.Schedule;

public static class SystemdServiceUnit
{
    public static string Build(
        string description,
        SystemdServiceUser serviceUser,
        string executable,
        params string[] arguments)
    {
        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
        return $"""
            [Unit]
            Description={description}

            [Service]
            Type=oneshot
            User={serviceUser.Name}
            Environment="HOME={Escape(serviceUser.HomeDirectory)}"
            ExecStart={command}
            """;
    }

    private static string Quote(string value)
        => "\"" + Escape(value) + "\"";

    private static string Escape(string value)
        => value.Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}

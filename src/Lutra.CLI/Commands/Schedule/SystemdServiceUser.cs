using Lutra.Core.Configuration;

namespace Lutra.CLI.Commands.Schedule;

public sealed record SystemdServiceUser(string Name, string HomeDirectory)
{
    public static SystemdServiceUser Resolve(string? requestedUser, string passwdPath = "/etc/passwd")
    {
        if (string.IsNullOrWhiteSpace(requestedUser))
        {
            throw new ConfigurationException(
                "A non-root service identity is required. Re-run with --run-as <USER>.");
        }

        if (!File.Exists(passwdPath))
            throw new ConfigurationException($"Cannot resolve service users because '{passwdPath}' was not found.");

        foreach (var line in File.ReadLines(passwdPath))
        {
            var fields = line.Split(':');
            if (fields.Length < 7 || !fields[0].Equals(requestedUser, StringComparison.Ordinal))
                continue;

            if (!uint.TryParse(fields[2], out var uid))
                break;
            if (uid == 0)
                throw new ConfigurationException("Lutra scheduled services must not run as root.");
            if (!Path.IsPathFullyQualified(fields[5]))
            {
                throw new ConfigurationException(
                    $"Service user '{requestedUser}' does not have an absolute home directory.");
            }

            return new SystemdServiceUser(fields[0], fields[5]);
        }

        throw new ConfigurationException($"Service user '{requestedUser}' does not exist.");
    }
}

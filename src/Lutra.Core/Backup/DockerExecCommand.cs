namespace Lutra.Core.Backup;

/// <summary>
/// A command to run inside a Docker container via <c>docker exec</c>.
/// </summary>
public record DockerExecCommand(
    string ContainerName,
    string Command,
    string[] Arguments,
    Dictionary<string, string>? EnvironmentVariables = null
);

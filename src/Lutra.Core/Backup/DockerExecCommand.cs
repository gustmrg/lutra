using Lutra.Core.Processes;

namespace Lutra.Core.Backup;

/// <summary>
/// A command to run inside a Docker container via <c>docker exec</c>.
/// </summary>
public record DockerExecCommand(
    string ContainerName,
    string Command,
    string[] Arguments,
    Dictionary<string, string>? EnvironmentVariables = null)
{
    /// <summary>Builds the <c>docker exec</c> invocation for this command.</summary>
    public ProcessRequest ToProcessRequest(Stream? input = null, Stream? output = null)
    {
        var arguments = new List<string> { "exec" };
        if (input is not null)
            arguments.Add("-i");

        // `-e NAME` without a value forwards the variable from the docker client's
        // own environment, which keeps the value out of the process list.
        if (EnvironmentVariables is not null)
        {
            foreach (var name in EnvironmentVariables.Keys)
            {
                arguments.Add("-e");
                arguments.Add(name);
            }
        }

        arguments.Add(ContainerName);
        arguments.Add(Command);
        arguments.AddRange(Arguments);

        return new ProcessRequest("docker", arguments)
        {
            Environment = EnvironmentVariables,
            Input = input,
            Output = output
        };
    }
}

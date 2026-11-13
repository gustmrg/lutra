using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

/// <summary>
/// Builds the dump command for a database engine. The command must write the
/// dump to standard output.
/// </summary>
public interface IBackupProvider
{
    DatabaseType Type { get; }

    DockerExecCommand BuildDumpCommand(DatabaseTarget target, string backupId);

    /// <summary>
    /// Gets the backup file extension, including the leading dot and excluding
    /// any compression suffix (e.g. <c>.dump</c>, <c>.sql</c>).
    /// </summary>
    string GetFileExtension(DatabaseTarget target);
}

/// <summary>
/// A command to run inside a Docker container via <c>docker exec</c>.
/// </summary>
public record DockerExecCommand(
    string ContainerName,
    string Command,
    string[] Arguments,
    Dictionary<string, string>? EnvironmentVariables = null
);

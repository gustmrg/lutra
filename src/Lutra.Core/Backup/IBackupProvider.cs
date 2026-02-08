using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

/// <summary>
/// Defines the contract for database-specific backup command generation.
/// Each supported database type (PostgreSQL, SQL Server, MongoDB) has a corresponding
/// implementation that knows how to construct the appropriate dump command.
/// </summary>
public interface IBackupProvider
{
    /// <summary>
    /// Gets the database type this provider handles.
    /// </summary>
    DatabaseType Type { get; }

    /// <summary>
    /// Builds the Docker exec command specification for dumping the specified database target.
    /// The returned command includes the container name, executable, arguments, and any
    /// environment variables needed for authentication.
    /// </summary>
    /// <param name="target">
    /// The database target configuration containing container, database name,
    /// credentials, and format preferences.
    /// </param>
    /// <returns>
    /// A <see cref="DockerExecCommand"/> that can be passed to an <see cref="IProcessExecutor"/>
    /// to execute the dump inside the container.
    /// </returns>
    DockerExecCommand BuildDumpCommand(DatabaseTarget target);

    /// <summary>
    /// Returns the file extension for the backup file based on the database type and format.
    /// The extension includes the leading dot (e.g., <c>.dump</c>, <c>.sql</c>, <c>.bak</c>).
    /// </summary>
    /// <param name="target">
    /// The database target configuration, used to determine format-specific extensions.
    /// </param>
    /// <returns>The file extension string including the leading dot.</returns>
    string GetFileExtension(DatabaseTarget target);

    /// <summary>
    /// Gets a value indicating whether the dump command streams its output to standard out.
    /// When <see langword="false"/>, the dump creates a file inside the container that must
    /// be extracted separately using the path from <see cref="GetContainerBackupPath"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. Only SQL Server overrides this, as
    /// <c>BACKUP DATABASE</c> writes to a file on disk rather than stdout.
    /// </remarks>
    bool StreamsToStdout => true;

    /// <summary>
    /// Gets the file path inside the container where the backup is written when
    /// <see cref="StreamsToStdout"/> is <see langword="false"/>.
    /// </summary>
    /// <param name="target">The database target configuration.</param>
    /// <returns>
    /// The absolute path inside the container, or <see langword="null"/> if the dump
    /// streams to stdout.
    /// </returns>
    string? GetContainerBackupPath(DatabaseTarget target) => null;
}

/// <summary>
/// Represents a command to be executed inside a Docker container via <c>docker exec</c>.
/// </summary>
/// <param name="ContainerName">The Docker container name or ID.</param>
/// <param name="Command">The executable to run inside the container (e.g., <c>pg_dump</c>).</param>
/// <param name="Arguments">Command-line arguments for the executable.</param>
/// <param name="EnvironmentVariables">
/// Optional environment variables to set inside the container via <c>docker exec -e</c>.
/// Used to pass credentials without exposing them in command-line arguments.
/// </param>
public record DockerExecCommand(
    string ContainerName,
    string Command,
    string[] Arguments,
    Dictionary<string, string>? EnvironmentVariables = null
);

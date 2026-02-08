namespace Lutra.Core.Configuration;

/// <summary>
/// Configuration for a single database to be backed up.
/// </summary>
/// <remarks>
/// Each target represents one database running in a Docker container.
/// The backup system executes database-specific dump commands inside the
/// container via <c>docker exec</c>.
/// </remarks>
public class DatabaseTarget
{
    /// <summary>
    /// Gets the friendly name for this database target.
    /// </summary>
    /// <remarks>
    /// Used in filenames, CLI commands, and the backup directory structure.
    /// Example: <c>example-db</c>, <c>finance-db</c>.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the type of database (PostgreSQL, SQL Server, or MongoDB).
    /// </summary>
    /// <remarks>
    /// Determines which <see cref="Backup.IBackupProvider"/> implementation
    /// is used to generate the backup command.
    /// </remarks>
    public required DatabaseType Type { get; init; }

    /// <summary>
    /// Gets the Docker container name or ID where the database is running.
    /// </summary>
    public required string Container { get; init; }

    /// <summary>
    /// Gets the name of the database to back up within the container.
    /// </summary>
    public required string Database { get; init; }

    /// <summary>
    /// Gets the database username for authentication.
    /// </summary>
    /// <remarks>
    /// Required for PostgreSQL and SQL Server. Optional for MongoDB.
    /// If not specified, database-specific defaults are used (e.g., <c>sa</c> for SQL Server).
    /// </remarks>
    public string? Username { get; init; }

    /// <summary>
    /// Gets the name of the environment variable containing the database password.
    /// </summary>
    /// <remarks>
    /// The password is not stored in the configuration file. Instead, this property
    /// references an environment variable name (e.g., <c>LUTRA_POSTGRES_PASSWORD</c>)
    /// which is resolved at runtime, typically from <c>/etc/lutra/.env</c>.
    /// </remarks>
    public string? PasswordEnv { get; init; }

    /// <summary>
    /// Gets the cron expression defining when this database should be backed up.
    /// </summary>
    /// <remarks>
    /// Used when generating systemd timer files via <c>lutra schedule install</c>.
    /// Defaults to <c>"0 3 * * *"</c> (3:00 AM daily).
    /// </remarks>
    public string Schedule { get; init; } = "0 3 * * *";

    /// <summary>
    /// Gets the database-specific dump format.
    /// </summary>
    /// <remarks>
    /// For PostgreSQL: <c>"custom"</c> (produces <c>.dump</c> files) or
    /// <c>"plain"</c> (produces <c>.sql</c> files). Defaults to <c>"custom"</c>.
    /// Ignored for SQL Server and MongoDB.
    /// </remarks>
    public string? Format { get; init; }

    /// <summary>
    /// Gets the compression type to apply to the backup output.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CompressionType.Gzip"/>. Set to
    /// <see cref="CompressionType.None"/> to disable compression.
    /// </remarks>
    public CompressionType Compression { get; init; } = CompressionType.Gzip;

    /// <summary>
    /// Gets the target-specific retention policy, overriding the global default.
    /// </summary>
    /// <remarks>
    /// If <see langword="null"/>, the global <see cref="BackupConfig.Retention"/>
    /// policy is used. Set this to apply different retention rules to individual
    /// databases (e.g., keep production backups longer than development backups).
    /// </remarks>
    public RetentionPolicy? Retention { get; init; }
}

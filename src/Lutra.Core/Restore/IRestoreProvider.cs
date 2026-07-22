using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Restore;

/// <summary>
/// Defines the contract for database-specific restore command generation.
/// Each supported database type (PostgreSQL, SQL Server, MongoDB) has a corresponding
/// implementation that knows how to construct the appropriate restore commands.
/// </summary>
/// <remarks>
/// When the destination database equals <see cref="DatabaseTarget.Database"/>, the
/// restore is destructive: providers must replace the existing contents
/// (e.g. <c>pg_restore --clean</c>, <c>mongorestore --drop</c>,
/// <c>RESTORE DATABASE ... WITH REPLACE</c>). When the destination differs,
/// the restore is a non-destructive test-restore into a temporary database.
/// </remarks>
public interface IRestoreProvider
{
    /// <summary>
    /// Gets the database type this provider handles.
    /// </summary>
    DatabaseType Type { get; }

    /// <summary>
    /// Gets a value indicating whether the restore command reads backup data from
    /// standard input. When <see langword="false"/>, the backup file must be uploaded
    /// into the container first (SQL Server).
    /// </summary>
    bool ReadsFromStdin { get; }

    /// <summary>
    /// Builds the Docker exec command that restores backup data read from standard
    /// input into <paramref name="destinationDatabase"/>.
    /// </summary>
    DockerExecCommand BuildStdinRestoreCommand(
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source)
        => throw new NotSupportedException($"{GetType().Name} does not support stdin-based restore.");

    /// <summary>
    /// Returns a unique file path inside the container where a backup file can be
    /// uploaded for file-based restores.
    /// </summary>
    string GetContainerRestoreFilePath(DatabaseTarget target, string restoreId)
        => throw new NotSupportedException($"{GetType().Name} does not support file-based restore.");

    /// <summary>
    /// Builds a command to create an empty database before restore, when the database
    /// engine requires it (PostgreSQL). Returns <see langword="null"/> when the restore
    /// itself creates the database (MongoDB, SQL Server).
    /// </summary>
    DockerExecCommand? BuildCreateDatabaseCommand(DatabaseTarget target, string database) => null;

    /// <summary>
    /// Builds a command that lists the logical files contained in a backup file,
    /// used to build MOVE clauses when restoring to a different database name.
    /// </summary>
    DockerExecCommand? BuildListBackupFilesCommand(DatabaseTarget target, string containerFilePath) => null;

    /// <summary>
    /// Parses the output of the command built by <see cref="BuildListBackupFilesCommand"/>.
    /// </summary>
    IReadOnlyList<BackupFileEntry> ParseBackupFileList(string commandOutput) => [];

    /// <summary>
    /// Builds the Docker exec command that restores from a backup file already present
    /// inside the container into <paramref name="destinationDatabase"/>.
    /// </summary>
    DockerExecCommand BuildContainerFileRestoreCommand(
        DatabaseTarget target,
        string containerFilePath,
        string destinationDatabase,
        IReadOnlyList<BackupFileEntry> backupFiles)
        => throw new NotSupportedException($"{GetType().Name} does not support file-based restore.");

    /// <summary>
    /// Builds commands to run before a destructive stdin restore, e.g. dropping and
    /// recreating the database for plain-format dumps that cannot replace existing
    /// objects. Empty by default.
    /// </summary>
    IEnumerable<DockerExecCommand> BuildDestructivePrepareCommands(
        DatabaseTarget target,
        RestoreSource source) => [];

    /// <summary>
    /// Builds a minimal validation command run against the restored database
    /// (e.g. counting user tables or collections).
    /// </summary>
    DockerExecCommand BuildValidationCommand(DatabaseTarget target, string database);

    /// <summary>
    /// Builds a command that drops the given database. Used to clean up temporary
    /// databases after a test-restore.
    /// </summary>
    DockerExecCommand BuildDropDatabaseCommand(DatabaseTarget target, string database);

    /// <summary>
    /// Generates the temporary database name used for a test-restore.
    /// </summary>
    string GenerateTestDatabaseName(DatabaseTarget target, string restoreId);
}

/// <summary>
/// Describes a backup file being used as a restore source.
/// </summary>
/// <param name="FilePath">The absolute path of the backup file on the host.</param>
/// <param name="Extension">
/// The backup format extension including the leading dot, excluding any compression
/// suffix (e.g. <c>.dump</c>, <c>.sql</c>, <c>.bak</c>, <c>.archive</c>).
/// </param>
/// <param name="IsCompressed">Whether the file is gzip-compressed.</param>
public record RestoreSource(string FilePath, string Extension, bool IsCompressed);

/// <summary>
/// A logical file contained in a database backup (SQL Server FILELISTONLY).
/// </summary>
/// <param name="LogicalName">The logical file name inside the backup.</param>
/// <param name="FileType"><c>"D"</c> for data files, <c>"L"</c> for log files.</param>
public record BackupFileEntry(string LogicalName, string FileType);

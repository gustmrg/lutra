using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Restore;

/// <summary>
/// Builds the restore commands for a database engine. Backup data is always fed
/// to the restore command through standard input.
/// </summary>
/// <remarks>
/// When the destination database equals <see cref="DatabaseTarget.Database"/>, the
/// restore is destructive and must replace the existing contents
/// (e.g. <c>pg_restore --clean</c>). When the destination differs, the restore is
/// a non-destructive test-restore into a temporary database.
/// </remarks>
public interface IRestoreProvider
{
    DatabaseType Type { get; }

    DockerExecCommand BuildStdinRestoreCommand(
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source);

    /// <summary>Builds the command that creates an empty database before a test-restore.</summary>
    DockerExecCommand BuildCreateDatabaseCommand(DatabaseTarget target, string database);

    /// <summary>
    /// Builds commands to run before a destructive restore, e.g. dropping and
    /// recreating the database for plain-format dumps that cannot replace existing
    /// objects.
    /// </summary>
    IEnumerable<DockerExecCommand> BuildDestructivePrepareCommands(
        DatabaseTarget target,
        RestoreSource source);

    /// <summary>Builds a minimal validation query run against the restored database.</summary>
    DockerExecCommand BuildValidationCommand(DatabaseTarget target, string database);

    DockerExecCommand BuildDropDatabaseCommand(DatabaseTarget target, string database);

    /// <summary>Generates the temporary database name used for a test-restore.</summary>
    string GenerateTestDatabaseName(DatabaseTarget target, string restoreId);
}

/// <summary>
/// Describes a backup file being used as a restore source.
/// </summary>
/// <param name="FilePath">The absolute path of the backup file on the host.</param>
/// <param name="Extension">
/// The backup format extension including the leading dot, excluding any compression
/// suffix (e.g. <c>.dump</c>, <c>.sql</c>).
/// </param>
/// <param name="IsCompressed">Whether the file is gzip-compressed.</param>
public record RestoreSource(string FilePath, string Extension, bool IsCompressed);

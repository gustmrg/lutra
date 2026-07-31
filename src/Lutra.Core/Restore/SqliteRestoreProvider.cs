using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Restore;

public sealed class SqliteRestoreProvider : IRestoreProvider
{
    public DatabaseType Type => DatabaseType.SQLite;
    public bool ReadsFromStdin => true;

    public DockerExecCommand BuildStdinRestoreCommand(
        DatabaseTarget target, string destinationDatabase, RestoreSource source)
        => new(target.Container, "sh", ["-c", "cat > \"$1\"", "lutra", destinationDatabase]);

    public DockerExecCommand BuildValidationCommand(DatabaseTarget target, string database)
        => new(target.Container, "sqlite3", [database, "PRAGMA integrity_check;"]);

    public DockerExecCommand BuildDropDatabaseCommand(DatabaseTarget target, string database)
        => new(target.Container, "rm", ["-f", database]);

    public string GenerateTestDatabaseName(DatabaseTarget target, string restoreId)
        => $"/tmp/lutra_verify_{restoreId}.sqlite";
}

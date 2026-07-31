using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public sealed class SqliteBackupProvider : IBackupProvider
{
    public DatabaseType Type => DatabaseType.SQLite;
    public bool StreamsToStdout => false;

    public DockerExecCommand BuildDumpCommand(DatabaseTarget target, string backupId)
    {
        var output = GetContainerBackupPath(target, backupId)!;
        return new DockerExecCommand(
            target.Container,
            "sqlite3",
            [target.Database, $".backup '{output.Replace("'", "''", StringComparison.Ordinal)}'"]);
    }

    public string GetFileExtension(DatabaseTarget target) => ".sqlite";

    public string GetContainerBackupPath(DatabaseTarget target, string backupId)
        => $"/tmp/lutra_sqlite_{backupId}.db";
}

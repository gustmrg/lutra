using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Restore;

public class SqlServerRestoreProvider : IRestoreProvider
{
    private const string SqlcmdPath = "/opt/mssql-tools18/bin/sqlcmd";

    public DatabaseType Type => DatabaseType.SqlServer;

    public bool ReadsFromStdin => false;

    public string GetContainerRestoreFilePath(DatabaseTarget target, string restoreId)
    {
        var safeTargetName = new string(target.Name.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

        return $"/tmp/lutra_restore_{safeTargetName}_{restoreId}.bak";
    }

    public DockerExecCommand? BuildListBackupFilesCommand(DatabaseTarget target, string containerFilePath)
    {
        var args = BaseArgs(target);
        args.Add("-h-1");
        args.Add("-W");
        args.Add("-s");
        args.Add(",");
        args.Add("-b");
        args.Add("-Q");
        args.Add($"SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'{EscapeSql(containerFilePath)}'");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: SqlcmdPath,
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public IReadOnlyList<BackupFileEntry> ParseBackupFileList(string commandOutput)
    {
        var entries = new List<BackupFileEntry>();

        foreach (var line in commandOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3)
                continue;

            var logicalName = parts[0].Trim().Trim('"');
            var fileType = parts[2].Trim().Trim('"');

            if (fileType is "D" or "L")
                entries.Add(new BackupFileEntry(logicalName, fileType));
        }

        return entries;
    }

    public DockerExecCommand BuildContainerFileRestoreCommand(
        DatabaseTarget target,
        string containerFilePath,
        string destinationDatabase,
        IReadOnlyList<BackupFileEntry> backupFiles)
    {
        string restoreSql;

        if (destinationDatabase.Equals(target.Database, StringComparison.Ordinal))
        {
            // Destructive restore over the original database: files return to their
            // original locations, so no MOVE clauses are needed.
            restoreSql =
                $"RESTORE DATABASE {Bracket(destinationDatabase)} FROM DISK = N'{EscapeSql(containerFilePath)}' " +
                "WITH REPLACE, RECOVERY";
        }
        else
        {
            if (backupFiles.Count == 0)
            {
                throw new InvalidOperationException(
                    "No data or log files were found in the SQL Server backup; cannot restore to a new database name.");
            }

            var moves = new List<string>();
            var dataIndex = 0;
            var logIndex = 0;

            foreach (var file in backupFiles)
            {
                string physicalPath;
                if (file.FileType == "D")
                {
                    var extension = dataIndex == 0 ? ".mdf" : ".ndf";
                    var suffix = dataIndex == 0 ? string.Empty : $"_{dataIndex}";
                    physicalPath = $"/var/opt/mssql/data/{destinationDatabase}{suffix}{extension}";
                    dataIndex++;
                }
                else
                {
                    var suffix = logIndex == 0 ? string.Empty : $"_{logIndex}";
                    physicalPath = $"/var/opt/mssql/data/{destinationDatabase}{suffix}.ldf";
                    logIndex++;
                }

                moves.Add($"MOVE N'{EscapeSql(file.LogicalName)}' TO N'{EscapeSql(physicalPath)}'");
            }

            restoreSql =
                $"RESTORE DATABASE {Bracket(destinationDatabase)} FROM DISK = N'{EscapeSql(containerFilePath)}' " +
                $"WITH {string.Join(", ", moves)}, REPLACE, RECOVERY";
        }

        var args = BaseArgs(target);
        args.Add("-b");
        args.Add("-Q");
        args.Add(restoreSql);

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: SqlcmdPath,
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public DockerExecCommand BuildValidationCommand(DatabaseTarget target, string database)
    {
        var args = BaseArgs(target);
        args.Add("-d");
        args.Add(database);
        args.Add("-h-1");
        args.Add("-W");
        args.Add("-b");
        args.Add("-Q");
        args.Add("SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: SqlcmdPath,
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public DockerExecCommand BuildDropDatabaseCommand(DatabaseTarget target, string database)
    {
        var args = BaseArgs(target);
        args.Add("-b");
        args.Add("-Q");
        args.Add($"DROP DATABASE IF EXISTS {Bracket(database)}");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: SqlcmdPath,
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public string GenerateTestDatabaseName(DatabaseTarget target, string restoreId)
        => $"lutra_verify_{restoreId}";

    private static List<string> BaseArgs(DatabaseTarget target)
    {
        return
        [
            "-S", "localhost",
            "-U", target.Username ?? "sa",
            "-C" // Trust server certificate
        ];
    }

    private static Dictionary<string, string>? BuildEnvVars(DatabaseTarget target)
    {
        if (target.PasswordEnv is null)
            return null;

        var password = Environment.GetEnvironmentVariable(target.PasswordEnv);
        return password is null
            ? null
            : new Dictionary<string, string> { ["SQLCMDPASSWORD"] = password };
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]" )}]";

    private static string EscapeSql(string value) => value.Replace("'", "''");
}

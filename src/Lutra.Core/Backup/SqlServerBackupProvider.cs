using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public class SqlServerBackupProvider : IBackupProvider
{
    public DatabaseType Type => DatabaseType.SqlServer;

    public bool StreamsToStdout => false;

    public string? GetContainerBackupPath(DatabaseTarget target, string backupId)
    {
        var safeTargetName = new string(target.Name.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

        return $"/tmp/lutra_{safeTargetName}_{backupId}.bak";
    }

    public DockerExecCommand BuildDumpCommand(DatabaseTarget target, string backupId)
    {
        var containerBackupPath = GetContainerBackupPath(target, backupId)
            ?? throw new InvalidOperationException("SQL Server backup path could not be generated.");

        var backupSql = target.SqlServerBackupKind switch
        {
            SqlServerBackupKind.Full =>
                $"BACKUP DATABASE [{target.Database}] TO DISK = N'{containerBackupPath}' WITH FORMAT, INIT, CHECKSUM",
            SqlServerBackupKind.Differential =>
                $"BACKUP DATABASE [{target.Database}] TO DISK = N'{containerBackupPath}' WITH DIFFERENTIAL, FORMAT, INIT, CHECKSUM",
            SqlServerBackupKind.Log =>
                $"BACKUP LOG [{target.Database}] TO DISK = N'{containerBackupPath}' WITH FORMAT, INIT, CHECKSUM",
            _ => throw new ArgumentOutOfRangeException()
        };

        var args = new List<string>
        {
            "-S", "localhost",
            "-U", target.Username ?? "sa",
            "-C", // Trust server certificate
            "-Q", backupSql
        };

        // Pass password via SQLCMDPASSWORD environment variable
        Dictionary<string, string>? envVars = null;
        if (target.PasswordEnv is not null)
        {
            var password = Environment.GetEnvironmentVariable(target.PasswordEnv);
            if (password is not null)
            {
                envVars = new Dictionary<string, string> { ["SQLCMDPASSWORD"] = password };
            }
        }

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "/opt/mssql-tools18/bin/sqlcmd",
            Arguments: args.ToArray(),
            EnvironmentVariables: envVars
        );
    }

    public string GetFileExtension(DatabaseTarget target) => target.SqlServerBackupKind switch
    {
        SqlServerBackupKind.Full => ".bak",
        SqlServerBackupKind.Differential => ".diff.bak",
        SqlServerBackupKind.Log => ".log.bak",
        _ => ".bak"
    };
}

using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public class SqlServerBackupProvider : IBackupProvider
{
    private const string ContainerBackupPath = "/tmp/lutra_backup.bak";

    public DatabaseType Type => DatabaseType.SqlServer;

    public bool StreamsToStdout => false;

    public string? GetContainerBackupPath(DatabaseTarget target) => ContainerBackupPath;

    public DockerExecCommand BuildDumpCommand(DatabaseTarget target)
    {
        var backupSql = $"BACKUP DATABASE [{target.Database}] TO DISK = N'{ContainerBackupPath}' WITH FORMAT, INIT";

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

    public string GetFileExtension(DatabaseTarget target) => ".bak";
}

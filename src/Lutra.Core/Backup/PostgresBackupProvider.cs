using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public class PostgresBackupProvider : IBackupProvider
{
    public DatabaseType Type => DatabaseType.PostgreSql;

    public DockerExecCommand BuildDumpCommand(DatabaseTarget target)
    {
        var args = new List<string>();

        if (target.Username is not null)
        {
            args.Add("-U");
            args.Add(target.Username);
        }

        // Custom format (-Fc) is the default; plain (-Fp) produces .sql
        var format = target.Format?.ToLowerInvariant() switch
        {
            "plain" => "-Fp",
            _ => "-Fc"
        };
        args.Add(format);

        args.Add(target.Database);

        // Pass password via PGPASSWORD environment variable inside the container
        Dictionary<string, string>? envVars = null;
        if (target.PasswordEnv is not null)
        {
            var password = Environment.GetEnvironmentVariable(target.PasswordEnv);
            if (password is not null)
            {
                envVars = new Dictionary<string, string> { ["PGPASSWORD"] = password };
            }
        }

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "pg_dump",
            Arguments: args.ToArray(),
            EnvironmentVariables: envVars
        );
    }

    public string GetFileExtension(DatabaseTarget target)
    {
        return target.Format?.ToLowerInvariant() switch
        {
            "plain" => ".sql",
            _ => ".dump"
        };
    }
}

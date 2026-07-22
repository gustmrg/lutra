using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Restore;

public class PostgresRestoreProvider : IRestoreProvider
{
    public DatabaseType Type => DatabaseType.PostgreSql;

    public bool ReadsFromStdin => true;

    public DockerExecCommand BuildStdinRestoreCommand(
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source)
    {
        if (source.Extension.Equals(".sql", StringComparison.OrdinalIgnoreCase))
        {
            var psqlArgs = BaseArgs(target);
            psqlArgs.Add("-d");
            psqlArgs.Add(destinationDatabase);
            psqlArgs.Add("-v");
            psqlArgs.Add("ON_ERROR_STOP=1");
            psqlArgs.Add("-q");

            return new DockerExecCommand(
                ContainerName: target.Container,
                Command: "psql",
                Arguments: psqlArgs.ToArray(),
                EnvironmentVariables: BuildEnvVars(target));
        }

        var args = BaseArgs(target);
        args.Add("-d");
        args.Add(destinationDatabase);
        args.Add("--clean");
        args.Add("--if-exists");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "pg_restore",
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public IEnumerable<DockerExecCommand> BuildDestructivePrepareCommands(
        DatabaseTarget target,
        RestoreSource source)
    {
        // Plain-format dumps contain no DROP statements, so a destructive restore
        // replaces the database by dropping and recreating it first.
        if (!source.Extension.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            return [];

        return
        [
            BuildDropDatabaseCommand(target, target.Database),
            BuildCreateDatabaseCommand(target, target.Database)!
        ];
    }

    public DockerExecCommand? BuildCreateDatabaseCommand(DatabaseTarget target, string database)
    {
        var args = BaseArgs(target);
        args.Add("-d");
        args.Add("postgres");
        args.Add("-c");
        args.Add($"CREATE DATABASE {QuoteIdentifier(database)}");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "psql",
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public DockerExecCommand BuildValidationCommand(DatabaseTarget target, string database)
    {
        var args = BaseArgs(target);
        args.Add("-d");
        args.Add(database);
        args.Add("-t");
        args.Add("-A");
        args.Add("-c");
        args.Add("SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "psql",
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public DockerExecCommand BuildDropDatabaseCommand(DatabaseTarget target, string database)
    {
        var args = BaseArgs(target);
        args.Add("-d");
        args.Add("postgres");
        args.Add("-c");
        args.Add($"DROP DATABASE IF EXISTS {QuoteIdentifier(database)} WITH (FORCE)");

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "psql",
            Arguments: args.ToArray(),
            EnvironmentVariables: BuildEnvVars(target));
    }

    public string GenerateTestDatabaseName(DatabaseTarget target, string restoreId)
        => $"lutra_verify_{restoreId}";

    private static List<string> BaseArgs(DatabaseTarget target)
    {
        var args = new List<string>();
        if (target.Username is not null)
        {
            args.Add("-U");
            args.Add(target.Username);
        }
        return args;
    }

    private static Dictionary<string, string>? BuildEnvVars(DatabaseTarget target)
    {
        if (target.PasswordEnv is null)
            return null;

        var password = Environment.GetEnvironmentVariable(target.PasswordEnv);
        return password is null
            ? null
            : new Dictionary<string, string> { ["PGPASSWORD"] = password };
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

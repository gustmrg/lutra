using Lutra.Core.Backup;
using Lutra.Core.Configuration;
using Lutra.Core.Restore;

namespace Lutra.Core.Postgres;

/// <summary>
/// Builds the <c>pg_dump</c>, <c>pg_restore</c> and <c>psql</c> invocations Lutra
/// runs inside a PostgreSQL container.
/// </summary>
public static class PostgresCommands
{
    /// <summary>Builds the dump command. The dump is written to standard output.</summary>
    public static DockerExecCommand Dump(DatabaseTarget target)
    {
        var args = BaseArgs(target);
        args.Add(IsPlainFormat(target) ? "-Fp" : "-Fc");
        args.Add(target.Database);
        return Command(target, "pg_dump", args);
    }

    /// <summary>
    /// Gets the dump file extension, including the leading dot and excluding any
    /// compression suffix.
    /// </summary>
    public static string DumpFileExtension(DatabaseTarget target)
        => IsPlainFormat(target) ? ".sql" : ".dump";

    /// <summary>
    /// Builds the command that restores a dump read from standard input into
    /// <paramref name="destinationDatabase"/>.
    /// </summary>
    public static DockerExecCommand Restore(
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source)
    {
        var args = BaseArgs(target);
        args.Add("-d");
        args.Add(destinationDatabase);

        if (IsPlainDump(source))
        {
            args.Add("-v");
            args.Add("ON_ERROR_STOP=1");
            args.Add("-q");
            return Command(target, "psql", args);
        }

        args.Add("--clean");
        args.Add("--if-exists");
        return Command(target, "pg_restore", args);
    }

    /// <summary>
    /// Builds the commands to run before restoring over the target's own database.
    /// </summary>
    public static IReadOnlyList<DockerExecCommand> PrepareDestructiveRestore(
        DatabaseTarget target,
        RestoreSource source)
    {
        // Plain-format dumps contain no DROP statements, so a destructive restore
        // replaces the database by dropping and recreating it first.
        if (!IsPlainDump(source))
            return [];

        return
        [
            DropDatabase(target, target.Database),
            CreateDatabase(target, target.Database)
        ];
    }

    public static DockerExecCommand CreateDatabase(DatabaseTarget target, string database)
        => Sql(target, "postgres", $"CREATE DATABASE {QuoteIdentifier(database)}");

    public static DockerExecCommand DropDatabase(DatabaseTarget target, string database)
        => Sql(target, "postgres", $"DROP DATABASE IF EXISTS {QuoteIdentifier(database)} WITH (FORCE)");

    /// <summary>Builds a query that prints the number of user tables in a database.</summary>
    public static DockerExecCommand CountUserTables(DatabaseTarget target, string database)
    {
        var args = BaseArgs(target);
        args.AddRange(
        [
            "-d", database, "-t", "-A", "-c",
            "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')"
        ]);
        return Command(target, "psql", args);
    }

    /// <summary>Generates the name of the temporary database used by a test-restore.</summary>
    public static string TestDatabaseName(string restoreId) => $"lutra_verify_{restoreId}";

    private static DockerExecCommand Sql(DatabaseTarget target, string database, string statement)
    {
        var args = BaseArgs(target);
        args.AddRange(["-d", database, "-c", statement]);
        return Command(target, "psql", args);
    }

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

    private static DockerExecCommand Command(DatabaseTarget target, string executable, List<string> args)
        => new(target.Container, executable, args.ToArray(), PasswordEnvironment(target));

    private static Dictionary<string, string>? PasswordEnvironment(DatabaseTarget target)
    {
        if (target.PasswordEnv is null)
            return null;

        var password = Environment.GetEnvironmentVariable(target.PasswordEnv);
        return password is null
            ? null
            : new Dictionary<string, string> { ["PGPASSWORD"] = password };
    }

    private static bool IsPlainFormat(DatabaseTarget target)
        => string.Equals(target.Format, "plain", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainDump(RestoreSource source)
        => source.Extension.Equals(".sql", StringComparison.OrdinalIgnoreCase);

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

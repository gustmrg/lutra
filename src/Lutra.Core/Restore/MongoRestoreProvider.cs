using Lutra.Core.Backup;
using Lutra.Core.Configuration;

namespace Lutra.Core.Restore;

public class MongoRestoreProvider : IRestoreProvider
{
    public DatabaseType Type => DatabaseType.MongoDb;

    public bool ReadsFromStdin => true;

    public DockerExecCommand BuildStdinRestoreCommand(
        DatabaseTarget target,
        string destinationDatabase,
        RestoreSource source)
    {
        var args = new List<string> { "--archive" };

        if (target.MongoOplog)
        {
            if (!destinationDatabase.Equals(target.Database, StringComparison.Ordinal))
                throw new NotSupportedException(
                    "Oplog archives cannot be namespace-remapped for an in-place test restore. Verify them in a disposable replica set.");
            args.Add("--drop");
            args.Add("--oplogReplay");
        }
        else if (destinationDatabase.Equals(target.Database, StringComparison.Ordinal))
        {
            // Destructive restore: drop existing collections before restoring.
            args.Add("--db");
            args.Add(destinationDatabase);
            args.Add("--drop");
        }
        else
        {
            // Test-restore: remap all namespaces into the temporary database.
            args.Add("--nsFrom");
            args.Add($"{target.Database}.*");
            args.Add("--nsTo");
            args.Add($"{destinationDatabase}.*");
        }

        AddAuthArgs(args, target);

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "mongorestore",
            Arguments: args.ToArray());
    }

    public DockerExecCommand BuildValidationCommand(DatabaseTarget target, string database)
        => BuildShellEvalCommand(target, $"db.getSiblingDB('{EscapeJs(database)}').getCollectionNames().length");

    public DockerExecCommand BuildDropDatabaseCommand(DatabaseTarget target, string database)
        => BuildShellEvalCommand(target, $"db.getSiblingDB('{EscapeJs(database)}').dropDatabase()");

    public string GenerateTestDatabaseName(DatabaseTarget target, string restoreId)
        => $"lutra_verify_{restoreId}";

    /// <summary>
    /// Builds an eval command that uses mongosh when available, falling back to the
    /// legacy mongo shell found in older (pre-6.0) MongoDB images.
    /// </summary>
    private static DockerExecCommand BuildShellEvalCommand(DatabaseTarget target, string evalScript)
    {
        var auth = BuildAuthArgsText(target);
        var script =
            "if command -v mongosh >/dev/null 2>&1; then " +
            $"mongosh --quiet{auth} --eval \"{evalScript}\"; " +
            $"else mongo --quiet{auth} --eval \"{evalScript}\"; fi";

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "sh",
            Arguments: ["-c", script]);
    }

    private static void AddAuthArgs(List<string> args, DatabaseTarget target)
    {
        if (target.Username is not null)
        {
            args.Add("--username");
            args.Add(target.Username);
            args.Add("--authenticationDatabase");
            args.Add("admin");
        }

        if (target.PasswordEnv is not null)
        {
            var password = Environment.GetEnvironmentVariable(target.PasswordEnv);
            if (password is not null)
            {
                args.Add("--password");
                args.Add(password);
            }
        }
    }

    private static string BuildAuthArgsText(DatabaseTarget target)
    {
        var args = new List<string>();
        AddAuthArgs(args, target);
        if (args.Count == 0)
            return string.Empty;

        // Shell-quote each argument so values with spaces survive the sh -c wrapper.
        return " " + string.Join(" ", args.Select(a => $"'{a.Replace("'", @"'\''")}'"));
    }

    private static string EscapeJs(string value) => value.Replace("'", "\\'");
}

using Lutra.Core.Configuration;

namespace Lutra.Core.Backup;

public class MongoBackupProvider : IBackupProvider
{
    public DatabaseType Type => DatabaseType.MongoDb;

    public DockerExecCommand BuildDumpCommand(DatabaseTarget target)
    {
        var args = new List<string>
        {
            "--archive", // Write to stdout as a single archive stream
            "--db", target.Database
        };

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

        return new DockerExecCommand(
            ContainerName: target.Container,
            Command: "mongodump",
            Arguments: args.ToArray()
        );
    }

    public string GetFileExtension(DatabaseTarget target) => ".archive";
}

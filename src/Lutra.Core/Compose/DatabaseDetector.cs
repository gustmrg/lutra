using Lutra.Core.Configuration;

namespace Lutra.Core.Compose;

public static class DatabaseDetector
{
    private static readonly (string Pattern, DatabaseType Type)[] KnownImages =
    [
        ("postgres:", DatabaseType.PostgreSql),
        ("bitnami/postgresql:", DatabaseType.PostgreSql),
        ("postgis/postgis:", DatabaseType.PostgreSql),
        ("mcr.microsoft.com/mssql/server:", DatabaseType.SqlServer),
        ("mcr.microsoft.com/azure-sql-edge:", DatabaseType.SqlServer),
        ("mongo:", DatabaseType.MongoDb),
        ("bitnami/mongodb:", DatabaseType.MongoDb),
    ];

    public static List<DetectedDatabase> Detect(ComposeFile composeFile)
    {
        var results = new List<DetectedDatabase>();

        foreach (var service in composeFile.Services)
        {
            if (service.Image is null)
                continue;

            var matched = MatchImage(service.Image);
            if (matched is null)
                continue;

            var detected = matched.Value switch
            {
                DatabaseType.PostgreSql => DetectPostgres(service),
                DatabaseType.SqlServer => DetectSqlServer(service),
                DatabaseType.MongoDb => DetectMongo(service),
                _ => null
            };

            if (detected is not null)
                results.Add(detected);
        }

        return results;
    }

    private static DatabaseType? MatchImage(string image)
    {
        foreach (var (pattern, type) in KnownImages)
        {
            if (image.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                image.Equals(pattern.TrimEnd(':'), StringComparison.OrdinalIgnoreCase))
                return type;
        }

        return null;
    }

    private static DetectedDatabase DetectPostgres(ComposeService service)
    {
        service.Environment.TryGetValue("POSTGRES_DB", out var dbName);
        service.Environment.TryGetValue("POSTGRES_USER", out var username);
        var passwordEnv = FindEnvKey(service, "POSTGRES_PASSWORD");

        return new DetectedDatabase
        {
            ServiceName = service.ServiceName,
            Type = DatabaseType.PostgreSql,
            ContainerName = service.ContainerName ?? service.ServiceName,
            DatabaseName = dbName ?? "postgres",
            Username = username ?? "postgres",
            PasswordEnvVar = passwordEnv,
            ImageName = service.Image,
            Confidence = DetectionConfidence.High
        };
    }

    private static DetectedDatabase DetectSqlServer(ComposeService service)
    {
        var passwordEnv = FindEnvKey(service, "MSSQL_SA_PASSWORD", "SA_PASSWORD");

        return new DetectedDatabase
        {
            ServiceName = service.ServiceName,
            Type = DatabaseType.SqlServer,
            ContainerName = service.ContainerName ?? service.ServiceName,
            DatabaseName = null,
            Username = "sa",
            PasswordEnvVar = passwordEnv,
            ImageName = service.Image,
            Confidence = DetectionConfidence.Medium
        };
    }

    private static DetectedDatabase DetectMongo(ComposeService service)
    {
        service.Environment.TryGetValue("MONGO_INITDB_DATABASE", out var dbName);
        service.Environment.TryGetValue("MONGO_INITDB_ROOT_USERNAME", out var username);
        var passwordEnv = FindEnvKey(service, "MONGO_INITDB_ROOT_PASSWORD");

        var hasAuth = username is not null || passwordEnv is not null;

        return new DetectedDatabase
        {
            ServiceName = service.ServiceName,
            Type = DatabaseType.MongoDb,
            ContainerName = service.ContainerName ?? service.ServiceName,
            DatabaseName = dbName,
            Username = username,
            PasswordEnvVar = passwordEnv,
            ImageName = service.Image,
            Confidence = dbName is not null ? DetectionConfidence.High : DetectionConfidence.Medium
        };
    }

    private static string? FindEnvKey(ComposeService service, params string[] candidates)
    {
        foreach (var key in candidates)
        {
            if (service.Environment.ContainsKey(key))
                return $"LUTRA_{service.ServiceName.ToUpperInvariant().Replace('-', '_')}_{key.Split('_')[^1]}";
        }

        return null;
    }
}

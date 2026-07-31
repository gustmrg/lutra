using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lutra.Core.Configuration;

/// <summary>
/// Loads backup configuration from a YAML file using YamlDotNet.
/// </summary>
public class YamlConfigLoader : IConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithTypeConverter(new CaseInsensitiveEnumConverter<DatabaseType>())
        .WithTypeConverter(new CaseInsensitiveEnumConverter<CompressionType>())
        .Build();

    /// <inheritdoc />
    public BackupConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new ConfigurationException($"Configuration file not found: {configPath}");

        string yaml;
        try
        {
            yaml = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"Cannot read configuration file '{configPath}': {ex.Message}", ex);
        }

        BackupConfig config;
        try
        {
            config = Deserializer.Deserialize<BackupConfig>(yaml)
                ?? throw new ConfigurationException("Configuration file is empty.");
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException($"Invalid YAML in configuration file: {ex.Message}", ex);
        }

        Validate(config);
        return config;
    }

    /// <summary>
    /// Loads environment variables from a <c>.env</c> file (KEY=VALUE format).
    /// Lines starting with <c>#</c> and blank lines are ignored.
    /// </summary>
    /// <param name="envFilePath">Path to the <c>.env</c> file. If the file does not exist, this method is a no-op.</param>
    public static void LoadEnvFile(string envFilePath)
    {
        if (!File.Exists(envFilePath))
            return;

        foreach (var line in File.ReadAllLines(envFilePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static void Validate(BackupConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BackupDirectory))
            throw new ConfigurationException("'backup_directory' is required.");

        ValidateRetention("retention", config.Retention);

        if (config.Databases.Count == 0 && config.Files.Count == 0)
            throw new ConfigurationException("At least one target must be configured under 'databases' or 'files'.");

        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < config.Databases.Count; i++)
        {
            var db = config.Databases[i];
            var prefix = $"databases[{i}]";

            if (string.IsNullOrWhiteSpace(db.Name))
                throw new ConfigurationException($"{prefix}: 'name' is required.");
            if (!IsSafeName(db.Name))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'name' cannot contain path separators.");
            if (!targetNames.Add(db.Name))
                throw new ConfigurationException($"{prefix} ({db.Name}): duplicate target name.");
            if (string.IsNullOrWhiteSpace(db.Container))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'container' is required.");
            if (string.IsNullOrWhiteSpace(db.Database))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'database' is required.");
            if (string.IsNullOrWhiteSpace(db.Schedule))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'schedule' is required.");
            if (LooksLikeCronExpression(db.Schedule))
                throw new ConfigurationException(
                    $"{prefix} ({db.Name}): 'schedule' looks like cron syntax. Use a systemd calendar expression such as \"*-*-* 03:00:00\".");

            if (db.Retention is not null)
                ValidateRetention($"{prefix} ({db.Name}).retention", db.Retention);

            if (db.PasswordEnv is not null && Environment.GetEnvironmentVariable(db.PasswordEnv) is null)
                throw new ConfigurationException(
                    $"{prefix} ({db.Name}): password_env '{db.PasswordEnv}' is not set in the environment or .env file.");

            switch (db.Type)
            {
                case DatabaseType.PostgreSql:
                    if (string.IsNullOrWhiteSpace(db.Username))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'username' is required for PostgreSQL.");
                    ValidatePostgresFormat(prefix, db);
                    break;
                case DatabaseType.SqlServer:
                    if (string.IsNullOrWhiteSpace(db.Username))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'username' is required for SQL Server.");
                    if (!string.IsNullOrWhiteSpace(db.Format))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'format' is only supported for PostgreSQL.");
                    break;
                case DatabaseType.MongoDb:
                    if (!string.IsNullOrWhiteSpace(db.Format))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'format' is only supported for PostgreSQL.");
                    break;
                default:
                    throw new ConfigurationException($"{prefix} ({db.Name}): unsupported database type '{db.Type}'.");
            }
        }

        if (config.Sync is { } sync)
        {
            if (!sync.Type.Equals("rsync", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException("sync: only type 'rsync' is supported.");
            if (string.IsNullOrWhiteSpace(sync.Host) || string.IsNullOrWhiteSpace(sync.User)
                || string.IsNullOrWhiteSpace(sync.DestinationPath) || string.IsNullOrWhiteSpace(sync.SshKeyPath))
                throw new ConfigurationException("sync: 'host', 'user', 'destination_path', and 'ssh_key_path' are required.");
            if (sync.Port is <= 0 or > 65535)
                throw new ConfigurationException("sync: 'port' must be between 1 and 65535.");
        }

        if (config.Notifications is { } notifications)
        {
            foreach (var url in notifications.Webhooks.Append(notifications.HealthchecksUrl)
                         .Where(url => !string.IsNullOrWhiteSpace(url)))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new ConfigurationException($"notifications: '{url}' is not a valid HTTP(S) URL.");
            }
        }

        if (config.Inventory is { } inventory)
        {
            if (string.IsNullOrWhiteSpace(inventory.Schedule))
                throw new ConfigurationException("inventory: 'schedule' is required.");
            if (LooksLikeCronExpression(inventory.Schedule))
                throw new ConfigurationException(
                    "inventory: 'schedule' looks like cron syntax. Use a systemd calendar expression such as \"*-*-* 04:00:00\".");

            string[] validCollectors = ["docker", "packages", "systemd", "crontabs", "firewall"];
            var unknownCollectors = inventory.Collectors?
                .Where(c => !validCollectors.Contains(c, StringComparer.OrdinalIgnoreCase))
                .ToList() ?? [];
            if (unknownCollectors.Count > 0)
                throw new ConfigurationException(
                    $"inventory: unknown collector(s): {string.Join(", ", unknownCollectors)}. Valid values: {string.Join(", ", validCollectors)}.");
        }

        for (var i = 0; i < config.Files.Count; i++)
        {
            var ft = config.Files[i];
            var prefix = $"files[{i}]";

            if (string.IsNullOrWhiteSpace(ft.Name))
                throw new ConfigurationException($"{prefix}: 'name' is required.");
            if (!IsSafeName(ft.Name))
                throw new ConfigurationException($"{prefix} ({ft.Name}): 'name' cannot contain path separators.");
            if (!targetNames.Add(ft.Name))
                throw new ConfigurationException($"{prefix} ({ft.Name}): duplicate target name.");
            if (ft.Paths.Count == 0)
                throw new ConfigurationException($"{prefix} ({ft.Name}): 'paths' must contain at least one entry.");
            if (ft.Paths.Any(string.IsNullOrWhiteSpace))
                throw new ConfigurationException($"{prefix} ({ft.Name}): 'paths' cannot contain empty entries.");
            if (string.IsNullOrWhiteSpace(ft.Schedule))
                throw new ConfigurationException($"{prefix} ({ft.Name}): 'schedule' is required.");
            if (LooksLikeCronExpression(ft.Schedule))
                throw new ConfigurationException(
                    $"{prefix} ({ft.Name}): 'schedule' looks like cron syntax. Use a systemd calendar expression such as \"*-*-* 03:00:00\".");

            if (ft.Retention is not null)
                ValidateRetention($"{prefix} ({ft.Name}).retention", ft.Retention);
        }
    }

    private static void ValidateRetention(string prefix, RetentionPolicy retention)
    {
        if (retention.MaxCount <= 0)
            throw new ConfigurationException($"{prefix}: 'max_count' must be greater than zero.");
        if (retention.MaxAgeDays <= 0)
            throw new ConfigurationException($"{prefix}: 'max_age_days' must be greater than zero.");
    }

    private static void ValidatePostgresFormat(string prefix, DatabaseTarget db)
    {
        if (string.IsNullOrWhiteSpace(db.Format))
            return;

        var format = db.Format.ToLowerInvariant();
        if (format is not ("custom" or "plain"))
            throw new ConfigurationException($"{prefix} ({db.Name}): PostgreSQL 'format' must be 'custom' or 'plain'.");
    }

    private static bool IsSafeName(string value)
    {
        return !value.Contains('/') && !value.Contains('\\');
    }

    private static bool LooksLikeCronExpression(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5;
    }

    /// <summary>
    /// Case-insensitive enum converter for YamlDotNet deserialization.
    /// </summary>
    private sealed class CaseInsensitiveEnumConverter<TEnum> : IYamlTypeConverter where TEnum : struct, Enum
    {
        public bool Accepts(Type type) => type == typeof(TEnum);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = parser.Consume<Scalar>();
            if (Enum.TryParse<TEnum>(scalar.Value, ignoreCase: true, out var result))
                return result;

            var validValues = string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()));
            throw new YamlException(scalar.Start, scalar.End,
                $"Invalid value '{scalar.Value}' for {typeof(TEnum).Name}. Valid values: {validValues}");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            emitter.Emit(new Scalar(value?.ToString()?.ToLowerInvariant() ?? string.Empty));
        }
    }
}

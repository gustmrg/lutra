using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Lutra.Core.Notifications;

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
        .WithTypeConverter(new CaseInsensitiveEnumConverter<RetentionMode>())
        .WithTypeConverter(new CaseInsensitiveEnumConverter<SqlServerBackupKind>())
        .Build();

    /// <inheritdoc />
    public BackupConfig Load(string configPath)
    {
        var normalizedConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(normalizedConfigPath))
            throw new ConfigurationException($"Configuration file not found: {configPath}");

        string yaml;
        try
        {
            yaml = File.ReadAllText(normalizedConfigPath);
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

        var stateDirectoryWasExplicit = !string.IsNullOrWhiteSpace(config.StateDirectory);
        config.ConfigPath = normalizedConfigPath;
        config.StateDirectoryWasExplicit = stateDirectoryWasExplicit;
        config.UsesStateDirectoryCompatibilityFallback =
            !stateDirectoryWasExplicit && !IsSystemConfigPath(normalizedConfigPath);
        config.StateDirectory = ResolveStateDirectory(
            config.StateDirectory,
            config.BackupDirectory,
            normalizedConfigPath);
        Validate(config);
        return config;
    }

    /// <summary>Resolves the application-state directory for new and legacy configurations.</summary>
    public static string ResolveStateDirectory(
        string? configuredStateDirectory,
        string backupDirectory,
        string configPath)
    {
        var normalizedConfigPath = Path.GetFullPath(configPath);
        var configDirectory = Path.GetDirectoryName(normalizedConfigPath)!;

        if (!string.IsNullOrWhiteSpace(configuredStateDirectory))
        {
            return Path.GetFullPath(configuredStateDirectory, configDirectory);
        }

        if (IsSystemConfigPath(normalizedConfigPath))
        {
            return "/var/lib/lutra";
        }

        var resolvedBackupDirectory = Path.GetFullPath(backupDirectory, configDirectory);
        return Path.Combine(resolvedBackupDirectory, ".lutra-state");
    }

    private static bool IsSystemConfigPath(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var systemConfigDirectory = Path.GetFullPath("/etc/lutra");
        return configDirectory.Equals(systemConfigDirectory, StringComparison.Ordinal)
            || configDirectory.StartsWith(
                systemConfigDirectory + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
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
        if (string.IsNullOrWhiteSpace(config.StateDirectory) || !Path.IsPathFullyQualified(config.StateDirectory))
            throw new ConfigurationException("'state_directory' must resolve to an absolute path.");

        ValidateRetention("retention", config.Retention);
        ValidateEncryption("encryption", config.Encryption);

        if (config.Databases.Count == 0 && config.Files.Count == 0 && config.Volumes.Count == 0)
            throw new ConfigurationException("At least one target must be configured under 'databases', 'files', or 'volumes'.");

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
            if (db.Name.Equals("@environment", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"{prefix} ({db.Name}): '@environment' is reserved for recovery history.");
            if (string.IsNullOrWhiteSpace(db.Container))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'container' is required.");
            if (string.IsNullOrWhiteSpace(db.Database))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'database' is required.");
            if (string.IsNullOrWhiteSpace(db.Schedule))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'schedule' is required.");
            if (LooksLikeCronExpression(db.Schedule))
                throw new ConfigurationException(
                    $"{prefix} ({db.Name}): 'schedule' looks like cron syntax. Use a systemd calendar expression such as \"*-*-* 03:00:00\".");
            if (db.VerifySchedule is not null && LooksLikeCronExpression(db.VerifySchedule))
                throw new ConfigurationException(
                    $"{prefix} ({db.Name}): 'verify_schedule' looks like cron syntax; use a systemd calendar expression.");

            if (db.Retention is not null)
                ValidateRetention($"{prefix} ({db.Name}).retention", db.Retention);
            ValidateEncryption($"{prefix} ({db.Name}).encryption", db.Encryption);

            if (db.PasswordEnv is not null && Environment.GetEnvironmentVariable(db.PasswordEnv) is null)
                throw new ConfigurationException(
                    $"{prefix} ({db.Name}): password_env '{db.PasswordEnv}' is not set in the environment or .env file.");

            switch (db.Type)
            {
                case DatabaseType.PostgreSql:
                    if (string.IsNullOrWhiteSpace(db.Username))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'username' is required for PostgreSQL.");
                    ValidatePostgresFormat(prefix, db);
                    if (db.MongoOplog || db.SqlServerBackupKind != SqlServerBackupKind.Full)
                        throw new ConfigurationException($"{prefix} ({db.Name}): MongoDB/SQL Server recovery options do not apply to PostgreSQL.");
                    if (db.PostgresWalArchivePath is not null && !targetNames.Add(db.Name + "-wal"))
                        throw new ConfigurationException($"{prefix} ({db.Name}): generated WAL target name '{db.Name}-wal' conflicts with another target.");
                    break;
                case DatabaseType.SqlServer:
                    if (string.IsNullOrWhiteSpace(db.Username))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'username' is required for SQL Server.");
                    if (!string.IsNullOrWhiteSpace(db.Format))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'format' is only supported for PostgreSQL.");
                    if (db.MongoOplog || db.PostgresWalArchivePath is not null)
                        throw new ConfigurationException($"{prefix} ({db.Name}): PostgreSQL/MongoDB recovery options do not apply to SQL Server.");
                    break;
                case DatabaseType.MongoDb:
                    if (!string.IsNullOrWhiteSpace(db.Format))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'format' is only supported for PostgreSQL.");
                    if (db.PostgresWalArchivePath is not null || db.SqlServerBackupKind != SqlServerBackupKind.Full)
                        throw new ConfigurationException($"{prefix} ({db.Name}): PostgreSQL/SQL Server recovery options do not apply to MongoDB.");
                    break;
                case DatabaseType.SQLite:
                    if (!string.IsNullOrWhiteSpace(db.Format))
                        throw new ConfigurationException($"{prefix} ({db.Name}): 'format' is only supported for PostgreSQL.");
                    if (db.MongoOplog || db.PostgresWalArchivePath is not null || db.SqlServerBackupKind != SqlServerBackupKind.Full)
                        throw new ConfigurationException($"{prefix} ({db.Name}): advanced recovery options do not apply to SQLite.");
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

        if (config.Notifications?.Discord is { } discord)
            DiscordWebhookUrlResolver.Resolve(discord);

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

        for (var i = 0; i < config.Volumes.Count; i++)
        {
            var volume = config.Volumes[i];
            var prefix = $"volumes[{i}]";
            if (string.IsNullOrWhiteSpace(volume.Name) || !IsSafeName(volume.Name))
                throw new ConfigurationException($"{prefix}: a safe 'name' is required.");
            if (!targetNames.Add(volume.Name))
                throw new ConfigurationException($"{prefix} ({volume.Name}): duplicate target name.");
            if (volume.Name.Equals("@environment", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"{prefix} ({volume.Name}): '@environment' is reserved for recovery history.");
            if (string.IsNullOrWhiteSpace(volume.Volume))
                throw new ConfigurationException($"{prefix} ({volume.Name}): 'volume' is required.");
            if (string.IsNullOrWhiteSpace(volume.Schedule) || LooksLikeCronExpression(volume.Schedule))
                throw new ConfigurationException($"{prefix} ({volume.Name}): use a valid systemd calendar 'schedule'.");
            if (volume.Retention is not null)
                ValidateRetention($"{prefix} ({volume.Name}).retention", volume.Retention);
            ValidateEncryption($"{prefix} ({volume.Name}).encryption", volume.Encryption);
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
            if (ft.Name.Equals("@environment", StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationException($"{prefix} ({ft.Name}): '@environment' is reserved for recovery history.");
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
            ValidateEncryption($"{prefix} ({ft.Name}).encryption", ft.Encryption);
        }

        ValidateEnvironment(config);
    }

    private static void ValidateEnvironment(BackupConfig config)
    {
        if (config.Environment is not { } environment)
            return;

        if (string.IsNullOrWhiteSpace(environment.Schedule)
            || LooksLikeCronExpression(environment.Schedule))
        {
            throw new ConfigurationException(
                "environment: use a valid systemd calendar 'schedule'.");
        }
        if (environment.Retention is not null)
            ValidateRetention("environment.retention", environment.Retention);
        if (environment.Exclude.Any(string.IsNullOrWhiteSpace))
            throw new ConfigurationException("environment: 'exclude' cannot contain empty patterns.");
        if (environment.SystemdUnits.Any(unit => !IsSafeSystemdUnit(unit)))
            throw new ConfigurationException(
                "environment: 'systemd_units' must contain simple .service unit names.");
        if (environment.DockerContainers.Any(name => !IsSafeRuntimeName(name)))
            throw new ConfigurationException(
                "environment: 'docker_containers' contains an invalid container name.");

        if (!environment.Enabled)
            return;
        if (!environment.AcknowledgePlaintext)
            throw new ConfigurationException(
                "environment: enabled plaintext recovery requires 'acknowledge_plaintext: true'.");
        if (environment.Targets.Count == 0)
            throw new ConfigurationException(
                "environment: enabled recovery requires at least one target.");

        var referencedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in environment.Targets)
        {
            if (string.IsNullOrWhiteSpace(name) || !referencedNames.Add(name))
                throw new ConfigurationException(
                    "environment: target names must be nonempty and unique.");

            var target = config.AllTargets().SingleOrDefault(
                candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                throw new ConfigurationException($"environment: target '{name}' is not configured.");
            if (target is DatabaseTarget)
                throw new ConfigurationException(
                    $"environment: database target '{name}' is not supported; use a file or volume target.");
        }
    }

    private static void ValidateEncryption(string prefix, Lutra.Core.Encryption.EncryptionConfig? encryption)
    {
        if (encryption is null)
            return;
        if (!encryption.Type.Equals("age", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException($"{prefix}: only type 'age' is supported.");
        if (string.IsNullOrWhiteSpace(encryption.Recipient)
            || !encryption.Recipient.StartsWith("age1", StringComparison.Ordinal))
            throw new ConfigurationException($"{prefix}: a valid age recipient public key is required.");
    }

    private static void ValidateRetention(string prefix, RetentionPolicy retention)
    {
        if (retention.MaxCount <= 0)
            throw new ConfigurationException($"{prefix}: 'max_count' must be greater than zero.");
        if (retention.MaxAgeDays <= 0)
            throw new ConfigurationException($"{prefix}: 'max_age_days' must be greater than zero.");
        if (retention.KeepAtLeast < 0)
            throw new ConfigurationException($"{prefix}: 'keep_at_least' cannot be negative.");
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

    private static bool IsSafeSystemdUnit(string value)
        => value.EndsWith(".service", StringComparison.Ordinal)
           && IsSafeRuntimeName(value);

    private static bool IsSafeRuntimeName(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.All(character => char.IsAsciiLetterOrDigit(character)
                                     || character is '_' or '-' or '.' or '@');

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

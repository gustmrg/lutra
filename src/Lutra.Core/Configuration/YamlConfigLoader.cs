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

        if (config.Databases is not { Count: > 0 })
            throw new ConfigurationException("At least one database target must be configured under 'databases'.");

        for (var i = 0; i < config.Databases.Count; i++)
        {
            var db = config.Databases[i];
            var prefix = $"databases[{i}]";

            if (string.IsNullOrWhiteSpace(db.Name))
                throw new ConfigurationException($"{prefix}: 'name' is required.");
            if (string.IsNullOrWhiteSpace(db.Container))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'container' is required.");
            if (string.IsNullOrWhiteSpace(db.Database))
                throw new ConfigurationException($"{prefix} ({db.Name}): 'database' is required.");
        }
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

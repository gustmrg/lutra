namespace Lutra.Core.Configuration;

/// <summary>
/// Loads and validates the Lutra backup configuration from a YAML file.
/// </summary>
/// <remarks>
/// Implementations handle YAML deserialization, snake_case-to-PascalCase mapping,
/// and validation of required fields. Credential resolution (via <c>password_env</c>
/// environment variables) happens at backup time, not at config load time.
/// </remarks>
public interface IConfigLoader
{
    /// <summary>
    /// Loads the backup configuration from the specified YAML file path.
    /// </summary>
    /// <param name="configPath">
    /// Absolute path to the YAML configuration file (typically <c>/etc/lutra/lutra.yaml</c>).
    /// </param>
    /// <returns>A fully populated <see cref="BackupConfig"/> ready for use by the backup orchestrator.</returns>
    /// <exception cref="ConfigurationException">
    /// Thrown when the file does not exist, cannot be parsed as valid YAML,
    /// or contains invalid or missing required values.
    /// </exception>
    BackupConfig Load(string configPath);
}

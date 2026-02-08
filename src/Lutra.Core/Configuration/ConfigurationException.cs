namespace Lutra.Core.Configuration;

/// <summary>
/// Thrown when the Lutra configuration file cannot be loaded, parsed, or contains invalid values.
/// </summary>
public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }

    public ConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

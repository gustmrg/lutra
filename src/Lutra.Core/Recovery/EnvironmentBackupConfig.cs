using Lutra.Core.Configuration;

namespace Lutra.Core.Recovery;

/// <summary>Configuration for a coherent, plaintext environment recovery set.</summary>
public sealed class EnvironmentBackupConfig
{
    /// <summary>Common secret-bearing names that environment backups always exclude from file targets.</summary>
    public static IReadOnlyList<string> MandatorySecretExcludes { get; } = Array.AsReadOnly<string>(
    [
        ".env",
        ".env.*",
        "*.key",
        "*.pem",
        "*.p12",
        "*.pfx",
        ".ssh",
        "id_rsa",
        "id_ed25519",
        "credentials*",
        "secrets*"
    ]);

    public bool Enabled { get; init; } = true;

    /// <summary>Required acknowledgement that recovery artifacts are not encrypted.</summary>
    public bool AcknowledgePlaintext { get; init; }

    public string Schedule { get; init; } = "Sun *-*-* 01:00:00";

    /// <summary>Names of existing file or volume targets to include.</summary>
    public List<string> Targets { get; init; } = [];

    /// <summary>Additional file archive exclusion patterns.</summary>
    public List<string> Exclude { get; init; } = [];

    public List<string> SystemdUnits { get; init; } = [];

    public List<string> DockerContainers { get; init; } = [];

    public RetentionPolicy? Retention { get; init; }
}

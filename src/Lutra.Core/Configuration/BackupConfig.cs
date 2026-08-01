using Lutra.Core.Encryption;
using Lutra.Core.Health;
using Lutra.Core.Inventory;
using Lutra.Core.Notifications;
using Lutra.Core.Sync;
using YamlDotNet.Serialization;

namespace Lutra.Core.Configuration;

/// <summary>
/// Root configuration for the Lutra backup system.
/// </summary>
/// <remarks>
/// This class represents the top-level configuration typically loaded from
/// <c>/etc/lutra/lutra.yaml</c>. It defines the global backup directory,
/// default retention policy, and the list of database targets to back up.
/// </remarks>
public class BackupConfig
{
    /// <summary>
    /// Gets the base directory where backup artifacts are stored.
    /// </summary>
    /// <remarks>
    /// Each database target gets its own subdirectory under this path.
    /// A legacy <c>backup-history.json</c> at the root is import-only and is
    /// not the authoritative application history store.
    /// </remarks>
    public required string BackupDirectory { get; init; }

    /// <summary>Gets the resolved directory for Lutra's local application state.</summary>
    /// <remarks>
    /// The configuration loader always resolves this value to an absolute path.
    /// It is separate from <see cref="BackupDirectory"/> so mutable application
    /// state is never treated as backup content.
    /// </remarks>
    public string? StateDirectory { get; set; }

    /// <summary>Gets the normalized path of the configuration that produced this model.</summary>
    [YamlIgnore]
    public string? ConfigPath { get; set; }

    /// <summary>Gets whether <c>state_directory</c> was present in the YAML.</summary>
    [YamlIgnore]
    public bool StateDirectoryWasExplicit { get; set; }

    /// <summary>Gets whether a custom config is using the legacy backup-local state fallback.</summary>
    [YamlIgnore]
    public bool UsesStateDirectoryCompatibilityFallback { get; set; }

    /// <summary>
    /// Gets the global retention policy applied to all database targets
    /// unless overridden by a target-specific policy.
    /// </summary>
    /// <seealso cref="DatabaseTarget.Retention"/>
    public required RetentionPolicy Retention { get; init; }

    /// <summary>Gets optional global age encryption inherited by targets.</summary>
    public EncryptionConfig? Encryption { get; init; }

    /// <summary>
    /// Gets the list of database targets to back up.
    /// </summary>
    public List<DatabaseTarget> Databases { get; init; } = [];

    /// <summary>
    /// Gets the list of file/configuration targets to back up.
    /// </summary>
    public List<FileTarget> Files { get; init; } = [];

    /// <summary>Gets named Docker volume targets.</summary>
    public List<VolumeTarget> Volumes { get; init; } = [];

    public HealthConfig? Health { get; init; }

    /// <summary>
    /// Gets optional server inventory snapshot configuration.
    /// </summary>
    public InventoryConfig? Inventory { get; init; }

    /// <summary>Gets optional webhook and Healthchecks.io notification settings.</summary>
    public NotificationConfig? Notifications { get; init; }

    /// <summary>Gets optional Raspberry Pi/offsite rsync settings.</summary>
    public RsyncConfig? Sync { get; init; }

    /// <summary>
    /// Returns all configured targets (databases first, then file targets).
    /// </summary>
    public IEnumerable<IBackupTarget> AllTargets()
        => Databases.Cast<IBackupTarget>().Concat(Files).Concat(Volumes);
}

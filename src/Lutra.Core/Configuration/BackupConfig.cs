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
    /// Gets the base directory where all backup files and history are stored.
    /// </summary>
    /// <remarks>
    /// Each database target gets its own subdirectory under this path.
    /// The backup history JSON file (<c>backup-history.json</c>) is stored
    /// at the root of this directory.
    /// </remarks>
    public required string BackupDirectory { get; init; }

    /// <summary>
    /// Gets the global retention policy applied to all database targets
    /// unless overridden by a target-specific policy.
    /// </summary>
    /// <seealso cref="DatabaseTarget.Retention"/>
    public required RetentionPolicy Retention { get; init; }

    /// <summary>
    /// Gets the list of database targets to back up.
    /// </summary>
    public required List<DatabaseTarget> Databases { get; init; }
}

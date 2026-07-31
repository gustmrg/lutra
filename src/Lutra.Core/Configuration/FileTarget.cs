using Lutra.Core.Encryption;

namespace Lutra.Core.Configuration;

/// <summary>
/// Configuration for a set of files and directories to be backed up as a
/// (optionally compressed) tar archive.
/// </summary>
/// <remarks>
/// File targets back up the configuration layer of a VPS: compose files, .env files,
/// reverse proxy configs, certificates, and similar. They run on the host and do not
/// involve Docker. System state (installed packages, users, firewall rules) should be
/// recreated, not backed up — do not point file targets at <c>/</c> or <c>/etc</c>
/// wholesale.
/// </remarks>
public class FileTarget : IBackupTarget
{
    /// <summary>
    /// Gets the friendly name for this file target.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the list of files and directories to include in the archive.
    /// Directories are archived recursively. Paths are stored in the archive
    /// relative to the filesystem root (without the leading <c>/</c>), so restoring
    /// to <c>/</c> recreates the original layout.
    /// </summary>
    public required List<string> Paths { get; init; }

    /// <summary>
    /// Gets optional exclude patterns. A pattern matches if it matches the full
    /// archive-relative path or any single path segment. Supported wildcards:
    /// <c>*</c> (any sequence of characters) and <c>?</c> (single character).
    /// Examples: <c>*.log</c>, <c>node_modules</c>, <c>etc/nginx/*.key</c>.
    /// </summary>
    public List<string>? Exclude { get; init; }

    /// <summary>
    /// Gets the systemd calendar expression defining when this target is backed up.
    /// Defaults to <c>"*-*-* 03:00:00"</c> (3:00 AM daily).
    /// </summary>
    public string Schedule { get; init; } = "*-*-* 03:00:00";

    /// <summary>
    /// Gets the compression type to apply to the archive. Defaults to
    /// <see cref="CompressionType.Gzip"/> (produces <c>.tar.gz</c>);
    /// <see cref="CompressionType.None"/> produces a plain <c>.tar</c>.
    /// </summary>
    public CompressionType Compression { get; init; } = CompressionType.Gzip;

    /// <summary>
    /// Gets the target-specific retention policy, overriding the global default.
    /// </summary>
    public RetentionPolicy? Retention { get; init; }

    public EncryptionConfig? Encryption { get; init; }
}

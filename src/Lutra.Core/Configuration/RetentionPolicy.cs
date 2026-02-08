namespace Lutra.Core.Configuration;

/// <summary>
/// Defines rules for automatically deleting old backup files.
/// </summary>
/// <remarks>
/// <para>
/// Lutra uses a conservative retention approach: backups are deleted only when
/// <strong>both</strong> conditions are met:
/// </para>
/// <list type="number">
/// <item>The number of successful backups exceeds <see cref="MaxCount"/></item>
/// <item>The backup age exceeds <see cref="MaxAgeDays"/></item>
/// </list>
/// <para>
/// This prevents accidental data loss if backups fail for an extended period
/// (old backups are preserved) or if many backups accumulate in a short time
/// (recent backups are kept even if count is high).
/// </para>
/// </remarks>
public class RetentionPolicy
{
    /// <summary>
    /// Gets the maximum number of successful backups to retain per database target.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>10</c>. Backups beyond this count are candidates for deletion
    /// if they also exceed <see cref="MaxAgeDays"/>.
    /// </remarks>
    public int MaxCount { get; init; } = 10;

    /// <summary>
    /// Gets the maximum age in days for retaining backups.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>30</c> days. Backups older than this are candidates for deletion
    /// if they also exceed the <see cref="MaxCount"/> threshold.
    /// </remarks>
    public int MaxAgeDays { get; init; } = 30;
}

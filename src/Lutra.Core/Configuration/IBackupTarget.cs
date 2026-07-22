namespace Lutra.Core.Configuration;

/// <summary>
/// Common contract for anything Lutra backs up on a schedule: database targets
/// (<see cref="DatabaseTarget"/>) and file/configuration targets (<see cref="FileTarget"/>).
/// </summary>
public interface IBackupTarget
{
    /// <summary>
    /// Gets the friendly name for this target, used in filenames, CLI commands,
    /// and the backup directory structure.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the systemd calendar expression defining when this target is backed up.
    /// </summary>
    string Schedule { get; }

    /// <summary>
    /// Gets the target-specific retention policy, or <see langword="null"/> to use
    /// the global default.
    /// </summary>
    RetentionPolicy? Retention { get; }
}

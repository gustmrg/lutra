namespace Lutra.Core.Inventory;

/// <summary>
/// Configuration for server inventory snapshots: a markdown document describing
/// what the server looked like (containers, packages, services, crontabs, firewall),
/// written alongside backups as a restoration aid.
/// </summary>
/// <remarks>
/// The inventory is not a backup of system state — it exists so rebuilding a VPS
/// does not depend on memory. When the <c>inventory</c> section is present in the
/// configuration file, snapshots are taken automatically after full backup runs and
/// on their own systemd timer.
/// </remarks>
public class InventoryConfig
{
    /// <summary>
    /// Gets whether inventory snapshots are enabled. Defaults to <see langword="true"/>
    /// when the section is present; set to <see langword="false"/> to disable without
    /// removing the section.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets the collectors to run. Valid values: <c>docker</c>, <c>packages</c>,
    /// <c>systemd</c>, <c>crontabs</c>, <c>firewall</c>. When <see langword="null"/>,
    /// all collectors run.
    /// </summary>
    public List<string>? Collectors { get; init; }

    /// <summary>
    /// Gets the systemd calendar expression for the inventory timer installed by
    /// <c>lutra schedule install</c>. Defaults to <c>"*-*-* 04:00:00"</c> (4:00 AM daily).
    /// </summary>
    public string Schedule { get; init; } = "*-*-* 04:00:00";
}

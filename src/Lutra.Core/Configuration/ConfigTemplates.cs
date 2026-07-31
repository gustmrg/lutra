namespace Lutra.Core.Configuration;

/// <summary>
/// Provides template content for initial configuration files.
/// </summary>
public static class ConfigTemplates
{
    /// <summary>
    /// Returns the default backup directory based on whether the process is running as root.
    /// </summary>
    public static string GetDefaultBackupDirectory()
    {
        return Environment.IsPrivilegedProcess
            ? "/var/backups/lutra"
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "backups", "lutra");
    }

    /// <summary>
    /// Returns the default config directory based on whether the process is running as root.
    /// </summary>
    public static string GetDefaultConfigDirectory()
    {
        return Environment.IsPrivilegedProcess
            ? "/etc/lutra"
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "lutra");
    }

    /// <summary>
    /// Generates the template YAML configuration content.
    /// </summary>
    public static string GenerateYamlTemplate(string backupDirectory)
    {
        return $"""
# Lutra Configuration File
# Documentation: https://github.com/gustmrg/lutra

backup_directory: {backupDirectory}

# Optional global encryption (public recipient only; never put the identity key here).
# encryption:
#   type: age
#   recipient: age1replace_with_your_public_recipient

retention:
  max_count: 10        # Keep at most 10 backups per target
  max_age_days: 30     # Age threshold
  mode: both            # both (conservative) or either
  keep_at_least: 1      # Always preserve this many newest backups

# Optional SSH/rsync offsite sync (Raspberry Pi or another SSH host).
# sync:
#   type: rsync
#   host: raspberrypi.local
#   user: lutra
#   destination_path: /srv/backups/lutra
#   ssh_key_path: /home/backup/.ssh/lutra_ed25519
#   port: 22
#   post_backup: true
#   delete: false
#   extra_args: []

# Lightweight notifications (optional). Generic webhooks receive JSON POSTs.
# Healthchecks.io receives a GET ping; failures append /fail to the URL.
# notifications:
#   webhooks:
#     - https://monitor.example/hooks/lutra
#   healthchecks_url: https://hc-ping.com/your-check-uuid

# Health check thresholds (optional — sensible defaults are built in)
# health:
#   min_samples: 5                 # Minimum backups needed for statistical analysis
#   window_size: 10                # Number of recent backups to analyze
#   size_deviation_threshold: 2.0  # Standard deviations for size anomaly
#   failure_streak_warning: 2      # Consecutive failures before warning
#   failure_streak_critical: 3     # Consecutive failures before critical alert

databases:
  # PostgreSQL Example
  - name: example-postgres
    type: postgresql
    container: postgres-container    # Docker container name
    database: mydb                   # Database name inside container
    username: postgres
    password_env: POSTGRES_PASSWORD  # Reference to env var in .env file
    schedule: "*-*-* 03:00:00"      # Daily at 3 AM (systemd calendar expression)
    # verify_schedule: "Sun *-*-* 05:00:00" # Optional restore drill timer
    # postgres_wal_archive_path: /srv/postgres-wal # archive_command output on host
    format: custom                   # custom (.dump) or plain (.sql)
    compression: gzip

  # MongoDB Example
  # - name: example-mongo
  #   type: mongodb
  #   container: mongo-container
  #   database: mydb
  #   schedule: "Sun *-*-* 04:00:00" # Weekly on Sundays at 4 AM
  #   compression: gzip
  #   # mongo_oplog: true             # replica-set-wide oplog-consistent archive

  # SQL Server Example
  # - name: example-sqlserver
  #   type: sqlserver
  #   container: sqlserver-container
  #   database: MyDatabase
  #   username: sa
  #   password_env: SQLSERVER_PASSWORD
  #   schedule: "*-*-* 02:00:00"    # Daily at 2 AM
  #   compression: gzip
  #   # sql_server_backup_kind: full  # full, differential, or log

# Optional server inventory snapshots (restoration aid, not system-state backup).
# Collector failures are recorded in the snapshot and never fail a backup run.
# inventory:
#   enabled: true
#   schedule: "*-*-* 04:00:00"
#   # Omit collectors to run all: docker, packages, systemd, crontabs, firewall
#   collectors:
#     - docker
#     - packages
#     - systemd
#     - crontabs
#     - firewall

# SQLite databases use the path inside the configured container.
# databases may also include:
#   - name: app-sqlite
#     type: sqlite
#     container: app-container
#     database: /data/app.db
#     schedule: "*-*-* 02:30:00"
#     compression: gzip

# Named Docker volumes are archived with a temporary Alpine helper container.
# volumes:
#   - name: app-uploads
#     volume: app_uploads
#     schedule: "*-*-* 03:15:00"
#     compression: gzip

# File targets back up configuration files as tar archives.
# Use them for compose files, .env files, reverse proxy configs, and certificates.
# Do NOT use them for system state (packages, users, firewall) — recreate that instead.
# files:
#   - name: app-config
#     paths:
#       - /opt/myapp                  # directories are archived recursively
#       - /etc/nginx/nginx.conf       # single files work too
#     exclude:
#       - "*.log"                     # glob patterns: * (any chars), ? (single char)
#       - node_modules                # also matches any path segment with this name
#     schedule: "*-*-* 03:30:00"
#     compression: gzip
""";
    }

    /// <summary>
    /// Generates the template .env file content.
    /// </summary>
    public static string GenerateEnvTemplate()
    {
        return """
# Lutra Environment Variables
# Store database passwords here (never commit this file!)

# Example PostgreSQL password
POSTGRES_PASSWORD=your-secret-password-here

# Example MongoDB password (if authentication is enabled)
# MONGO_PASSWORD=your-mongo-password

# Example SQL Server password
# SQLSERVER_PASSWORD=your-sqlserver-password
""";
    }
}
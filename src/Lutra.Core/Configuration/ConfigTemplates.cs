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

    /// <summary>Returns the state directory used by a newly generated configuration.</summary>
    public static string GetDefaultStateDirectory()
    {
        return ResolveDefaultStateDirectory(
            Environment.IsPrivilegedProcess,
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Resolves a new installation's state directory from its installation context.</summary>
    public static string ResolveDefaultStateDirectory(
        bool isPrivilegedProcess,
        string? xdgStateHome,
        string userProfile)
    {
        if (isPrivilegedProcess)
            return "/var/lib/lutra";

        if (!string.IsNullOrWhiteSpace(xdgStateHome) && Path.IsPathFullyQualified(xdgStateHome))
            return Path.Combine(xdgStateHome, "lutra");

        return Path.Combine(userProfile, ".local", "state", "lutra");
    }

    /// <summary>
    /// Generates the template YAML configuration content.
    /// </summary>
    public static string GenerateYamlTemplate(string backupDirectory, string? stateDirectory = null)
    {
        stateDirectory ??= GetDefaultStateDirectory();
        return $"""
# Lutra Configuration File
# Documentation: https://github.com/gustmrg/lutra

backup_directory: {backupDirectory}
state_directory: {stateDirectory}

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

# Optional best-effort Discord notifications. Store webhook URLs in .env, not YAML.
# notifications:
#   discord:
#     webhooks:
#       - url_env: LUTRA_DISCORD_WEBHOOK

databases:
  - name: example-postgres
    type: postgresql
    container: postgres-container    # Docker container name
    database: mydb                   # Database name inside container
    username: postgres
    password_env: POSTGRES_PASSWORD  # Reference to env var in .env file
    schedule: "*-*-* 03:00:00"      # Daily at 3 AM (systemd calendar expression)
    # verify_schedule: "Sun *-*-* 05:00:00" # Optional restore drill timer
    format: custom                   # custom (.dump) or plain (.sql)
    compression: gzip
""";
    }

    /// <summary>
    /// Generates the template .env file content.
    /// </summary>
    public static string GenerateEnvTemplate()
    {
        return """
# Lutra Environment Variables
# Store credentials here (never commit this file!)

# PostgreSQL password referenced by password_env in lutra.yaml
POSTGRES_PASSWORD=your-secret-password-here

# Optional Discord incoming webhook URL (treat it as a credential)
# LUTRA_DISCORD_WEBHOOK=https://discord.com/api/webhooks/replace-me
""";
    }
}

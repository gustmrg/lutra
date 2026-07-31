# 🦦 Lutra

**Automated database and configuration backups for a Docker-based VPS.**

Lutra is a CLI tool that automates backups for containerized databases and configuration files running on a Linux VPS. Built with C# (.NET 10.0) and [Spectre.Console](https://spectreconsole.net/), it ships as a single self-contained binary — no runtime dependencies required.

It uses `docker exec` to run native dump tools (`pg_dump`, `mongodump`, `sqlcmd`) inside your containers, and archives file targets (compose files, `.env` files, reverse proxy configs) into tar archives — all with optional gzip compression, checksums, manifests, and automatic retention. Scheduling is handled by systemd timers, not a custom daemon.

## Table of Contents

- [Supported Databases](#supported-databases)
- [Quick Start](#quick-start)
  - [Download Pre-built Binary](#download-pre-built-binary)
  - [Automated Setup](#automated-setup)
  - [Manual Setup](#manual-setup)
- [Commands](#commands)
  - [Global Options](#global-options)
- [Configuration Reference](#configuration-reference)
  - [Global Settings](#global-settings)
  - [Database Target Settings](#database-target-settings)
  - [File Target Settings](#file-target-settings)
  - [Server Inventory Settings](#server-inventory-settings)
  - [Full Example](#full-example)
- [How It Works](#how-it-works)
  - [Backup File Structure](#backup-file-structure)
  - [Retention Policy](#retention-policy)
- [Project Structure](#project-structure)
- [Downloading Backups to a Local Machine](#downloading-backups-to-a-local-machine)
- [Security](#security)
- [Requirements](#requirements)
- [Building from Source](#building-from-source)
- [Tech Stack](#tech-stack)
- [License](#license)

## Supported Databases

| Database   | Dump Tool    | Formats                          |
|------------|-------------|----------------------------------|
| PostgreSQL | `pg_dump`   | Custom (`.dump`), Plain (`.sql`) |
| SQL Server | `sqlcmd`    | Native backup (`.bak`)           |
| MongoDB    | `mongodump` | Archive (`.archive`)             |

## Quick Start

> **Note**: Lutra is in early development. Core backup, restore, and restore verification functionality is implemented.

### Download Pre-built Binary

Download the latest release from [GitHub Releases](https://github.com/gustmrg/lutra/releases/latest) — no .NET SDK or repo access required:

```bash
# Download and install (linux-x64)
curl -sLO https://github.com/gustmrg/lutra/releases/latest/download/lutra-linux-x64.tar.gz
tar -xzf lutra-linux-x64.tar.gz
sudo mv lutra /usr/local/bin/lutra
sudo chmod +x /usr/local/bin/lutra
```

For ARM-based servers (Oracle Cloud, AWS Graviton, etc.), use `lutra-linux-arm64.tar.gz` instead.

### Automated Setup

```bash
# Clone the repository
git clone https://github.com/gustmrg/lutra.git
cd lutra

# Run the setup script (builds, installs, and creates config templates)
sudo ./setup.sh      # System-wide installation (requires sudo)
# OR
./setup.sh           # User-only installation (~/.local/bin)
```

The setup script will:
- Download a pre-built binary from GitHub Releases (or build from source if .NET SDK is installed)
- Install the binary to `/usr/local/bin/lutra` (or `~/.local/bin/lutra`)
- Create configuration directories
- Generate template config and .env files
- Set proper permissions

Use `./setup.sh --from-release` to force downloading the pre-built binary even when .NET SDK is available.

> See [`lutra.example.yaml`](lutra.example.yaml) and [`.env.example`](.env.example) for full configuration examples.

### Manual Setup

If you prefer manual installation:

```bash
# Build from source
dotnet publish src/Lutra.CLI/Lutra.CLI.csproj \
  -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o dist/

# Install binary
sudo cp dist/Lutra.CLI /usr/local/bin/lutra
sudo chmod +x /usr/local/bin/lutra

# Create directories
sudo mkdir -p /etc/lutra /var/backups/lutra
sudo chown $USER:$USER /etc/lutra /var/backups/lutra
```

Create `/etc/lutra/lutra.yaml`:

```yaml
backup_directory: /var/backups/lutra

retention:
  max_count: 10
  max_age_days: 30

databases:
  - name: my-postgres
    type: postgresql
    container: my-postgres-container
    database: app_production
    username: postgres
    password_env: LUTRA_POSTGRES_PASSWORD
    schedule: "*-*-* 03:00:00"
    format: custom
    compression: gzip
```

### 3. Set credentials

```bash
# Add to /etc/lutra/.env (chmod 600)
LUTRA_POSTGRES_PASSWORD=your-secret-password
```

### 4. Run

```bash
# Validate configuration
lutra config validate

# Run a backup now
lutra backup run

# Install systemd timers for automated scheduling (requires sudo)
sudo lutra schedule install
```

## Commands

```bash
# Backup
lutra backup run                             # Back up all configured databases
lutra backup run --target my-postgres        # Back up a specific database
lutra backup list                            # List configured databases and schedules
lutra backup verify-file --file <PATH>       # Verify a backup file checksum and manifest
lutra backup reconcile                       # Compare backup files, sidecars, and history
lutra backup reconcile --target my-postgres  # Reconcile one target only
lutra backup reconcile --json                # Machine-readable reconciliation report

# Restore
lutra restore                                # Interactive restore (select DB → backup)
lutra restore --target my-postgres --file <PATH> --force  # Non-interactive restore
lutra verify                                 # Test-restore the latest backup of a DB
lutra verify --target my-postgres --file <PATH>           # Test-restore a specific backup

# History
lutra history                                # Show backup history for all targets
lutra history --target my-postgres           # Show history for specific target

# Maintenance
lutra cleanup                                # Remove old backups per retention policy
lutra cleanup --target my-postgres           # Clean up specific target
lutra cleanup --dry-run                      # Preview what would be deleted
lutra cleanup --orphan-sidecars              # Remove sidecars without backup files
lutra cleanup --orphan-files --force         # Explicitly remove untracked backup files
lutra cleanup --prune-history                # Prune old failures/verify/sync records
lutra health                                 # Analyze backup health and detect anomalies
lutra health --target my-postgres            # Analyze health for a specific target
lutra health --json                          # Machine-readable health report
lutra inventory                              # Capture a server inventory snapshot
lutra sync --dry-run                         # Preview an offsite rsync
lutra sync --target my-postgres              # Sync one target directory
lutra sync --validate                        # Validate SSH/rsync and remote write access
lutra sync --delete                          # Explicitly mirror local deletions remotely

# Configuration
lutra config init                            # Create config directories and template files
lutra config validate                        # Validate config file
lutra config validate --preflight            # Also check systemd, Docker, containers, and dump tools
lutra config generate                        # Generate config from docker-compose.yml
lutra config reset                           # Reset config files to template defaults

# Scheduling (systemd timers)
sudo lutra schedule install                  # Install systemd timers for all targets
sudo lutra schedule install --target my-postgres  # Install timer for specific target
lutra schedule list                          # List installed timers and their status
sudo lutra schedule remove                   # Remove all Lutra timer units
sudo lutra schedule remove --target my-postgres   # Remove timer for specific target

# Uninstall
sudo lutra uninstall                         # Remove all Lutra artifacts (config, timers, binary)
```

### Global Options

Most commands support these options:

```bash
--config <PATH>       # Path to config file (default: /etc/lutra/lutra.yaml)
--env-file <PATH>     # Path to .env file (default: /etc/lutra/.env)
```

## Configuration Reference

### Global Settings

| Property               | Type    | Default                | Description                           |
|------------------------|---------|------------------------|---------------------------------------|
| `backup_directory`     | string  | —                      | Base directory for all backup files   |
| `retention.max_count`  | integer | `10`                   | Max backups to keep per database      |
| `retention.max_age_days` | integer | `30`                 | Backup age threshold in days          |
| `retention.mode`         | enum    | `both`                | Delete when `both` or `either` limit matches |
| `retention.keep_at_least`| integer | `1`                   | Always preserve this many newest backups |
| `notifications.webhooks` | list | `[]` | JSON webhook endpoints for operation/health events |
| `notifications.healthchecks_url` | string | — | Healthchecks.io-compatible ping URL |
| `sync` | object | — | Optional SSH/rsync offsite destination |

Offsite sync is configured with `sync.type: rsync`, `host`, `user`, `destination_path`, and `ssh_key_path`; optional fields are `port`, `extra_args`, `post_backup`, and `delete`. Remote deletion is disabled unless enabled in configuration or explicitly requested with `--delete`. `--dry-run` is recommended before the first transfer. A successful transfer writes a local `.last-sync.json` marker used by health checks.

For a Raspberry Pi, create a dedicated restricted user, install its public SSH key, and grant write access only to the destination directory. A pull-based timer on the Pi is safer because compromise of the VPS cannot use its credentials to delete the Pi's repository. Restic, Borg, and Kopia remain compatible: point them at Lutra's `backup_directory` instead of enabling `sync`.

Notifications are best-effort and never change an operation's exit status. Generic webhooks receive event, status, summary, target, timestamp, and host fields. A configured Healthchecks.io URL is pinged directly for success and with `/fail` appended for failures. Backup, restore, verification, and unhealthy health-check events are supported.

### Database Target Settings

| Property       | Type       | Default        | Description                                    |
|----------------|-----------|----------------|------------------------------------------------|
| `name`         | string    | —              | Friendly name (used in filenames and commands) |
| `type`         | enum      | —              | `postgresql`, `sqlserver`, or `mongodb`        |
| `container`    | string    | —              | Docker container name or ID                    |
| `database`     | string    | —              | Database name inside the container             |
| `username`     | string    | —              | Database user (required for PG and SQL Server) |
| `password_env` | string    | —              | Environment variable name holding the password |
| `schedule`     | string    | `"*-*-* 03:00:00"` | Systemd calendar expression for timer generation |
| `verify_schedule` | string | — | Optional systemd schedule for non-destructive restore drills |
| `format`       | enum      | `custom`       | `custom` or `plain` (PostgreSQL only)          |
| `compression`  | enum      | `gzip`         | `gzip` or `none`                               |
| `retention`    | object    | global default | Override global retention for this target       |

### File Target Settings

File targets back up configuration files as tar archives. Use them for compose files, `.env` files, reverse proxy configs, and certificates. **Do not** use them for system state (installed packages, users, firewall rules) — recreate that during a rebuild instead of backing it up.

Defined under the top-level `files:` key (can be combined with, or replace, `databases:`):

| Property      | Type       | Default            | Description                                           |
|---------------|-----------|--------------------|-------------------------------------------------------|
| `name`        | string    | —                  | Friendly name (used in filenames and commands)        |
| `paths`       | list      | —                  | Files and/or directories to archive (dirs recursively) |
| `exclude`     | list      | —                  | Optional glob patterns (`*`, `?`); also matches any single path segment |
| `schedule`    | string    | `"*-*-* 03:00:00"` | Systemd calendar expression for timer generation      |
| `compression` | enum      | `gzip`             | `gzip` (`.tar.gz`) or `none` (`.tar`)                 |
| `retention`   | object    | global default     | Override global retention for this target             |

Paths are stored in the archive relative to the filesystem root, so `lutra restore` extracts them back to their original locations by default (or elsewhere with `--destination`).

```yaml
files:
  - name: app-config
    paths:
      - /opt/myapp
      - /etc/nginx
    exclude:
      - "*.log"
      - node_modules
    schedule: "*-*-* 03:30:00"
    compression: gzip
```

> **Secrets**: file targets often include `.env` files and private keys. `lutra config validate` warns when configured paths look sensitive. Backups are stored unencrypted — restrict access to the backup directory (encryption support is planned).

### Server Inventory Settings

An optional inventory snapshot records a small, human-readable server inventory under `<backup_directory>/inventory/`. It is a rebuild aid, not a backup of packages, users, firewall rules, or other system state. Snapshots run after an unfiltered `backup run` and can also use their own systemd timer.

```yaml
inventory:
  enabled: true
  schedule: "*-*-* 04:00:00"
  # Omit collectors to run all of them.
  collectors: [docker, packages, systemd, crontabs, firewall]
```

Collectors are best-effort: missing tools and command failures are written into the snapshot and do not fail database or file backups. Docker environment **names** are recorded but values are omitted; cron commands are also omitted to avoid capturing embedded credentials. Global retention settings apply to inventory snapshots.

### Full Example

```yaml
backup_directory: /var/backups/lutra

retention:
  max_count: 10
  max_age_days: 30

databases:
  - name: icon-db
    type: postgresql
    container: icon-postgres
    database: icon_production
    username: postgres
    password_env: LUTRA_ICON_DB_PASSWORD
    schedule: "*-*-* 03:00:00"
    format: custom
    compression: gzip
    retention:
      max_count: 15
      max_age_days: 60

  - name: finance-db
    type: sqlserver
    container: finance-sqlserver
    database: FinanceProduction
    username: sa
    password_env: LUTRA_FINANCE_DB_PASSWORD
    schedule: "*-*-* 02:00:00"
    compression: gzip

  - name: app-mongo
    type: mongodb
    container: app-mongo
    database: app_data
    schedule: "Sun *-*-* 04:00:00"  # Weekly on Sundays
    compression: gzip
```

## How It Works

Lutra runs **on the VPS** alongside your containers. It does not connect to databases over the network — it executes dump commands directly inside the containers via `docker exec`, streams the output to disk with optional compression, and tracks results in a local JSON history file.

```
┌──────────────────────────────────────────────────┐
│  VPS                                             │
│                                                  │
│  ┌──────────────┐   docker exec                  │
│  │    Lutra     │ ──────────────► pg_dump        │
│  │   (.NET 10)   │                 mongodump     │
│  │              │                 sqlcmd         │
│  │              │   writes to                    │
│  │  - backup    │ ──────────────► /var/backups/  │
│  │  - compress  │                                │
│  │  - rotate    │                                │
│  └──────────────┘                                │
│        ▲                                         │
│        │ systemd timer (scheduled)               │
└────────┼─────────────────────────────────────────┘
         │
         │ rsync (built-in optional sync) or external backup tool
         ▼
   Local Machine / Raspberry Pi
```

### Backup File Structure

```
/var/backups/lutra/
├── backup-history.json
├── inventory/
│   ├── inventory_2026-02-08_040000_a1b2c3d4e5f6.md
│   └── inventory_2026-02-08_040000_a1b2c3d4e5f6.md.sha256
├── example-db/
│   ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz
│   ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz.sha256
│   ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz.json
│   ├── example-db_2026-02-07_030000.dump.gz
│   └── example-db_2026-02-06_030000.dump.gz
├── finance-db/
│   ├── finance-db_2026-02-08_020000.bak.gz
│   └── finance-db_2026-02-07_020000.bak.gz
└── app-mongo/
    └── app-mongo_2026-02-02_040000.archive.gz
```

### Retention Policy

By default (`mode: both`), backups are deleted only when **both** conditions are met (conservative approach):
- The backup count exceeds `max_count` for that target
- The backup age exceeds `max_age_days`

Set `mode: either` to delete when either limit matches. `keep_at_least` always protects the configured number of newest successful backups. Per-target retention settings override global defaults. Inventory snapshots use the global policy. Cleanup deletes checksum and manifest sidecars together with each retained backup.

`cleanup --dry-run` previews every affected path. Orphan sidecars require the explicit `--orphan-sidecars` option. Untracked backup artifacts require `--orphan-files` plus interactive confirmation (or `--force`). `--prune-history` removes old failed attempts and operational verify/sync records while preserving successful backup history.

Successful backups also write integrity sidecars:
- `.sha256` stores the SHA-256 checksum for the backup file
- `.json` stores a manifest with target metadata, size, checksum, duration, format, compression, and Lutra version

`lutra health` checks recent backup age, failure streaks, size and duration anomalies, missing history-referenced files, and the latest artifact's checksum/sidecars. It exits `0` when healthy, `1` for warnings, and `2` for critical findings; use `--json` for monitoring integrations.

Use `lutra backup verify-file --file <PATH>` to verify a backup against its checksum and manifest sidecars. Use `lutra backup reconcile` for a read-only comparison of configured target directories with successful history entries. It reports untracked backup files, missing backup files, and missing checksum or manifest sidecars; exit code `1` means inconsistencies were found.

## Restoring and Verifying Backups

### `lutra restore` (destructive)

Restores a backup into the configured database, **replacing its current contents**. Requires an interactive confirmation unless `--force` is passed. Omitting `--target` and/or `--file` opens interactive selection prompts.

```bash
lutra restore --target my-postgres --file /var/backups/lutra/my-postgres/my-postgres_2026-02-08_030000_a1b2c3d4e5f6.dump.gz
```

Behavior per database:

| Database   | Restore mechanism                                                                 |
|------------|-----------------------------------------------------------------------------------|
| PostgreSQL | Custom format: `pg_restore --clean --if-exists`. Plain format: the database is dropped and recreated, then loaded with `psql`. |
| SQL Server | The `.bak` is streamed into the container, then `RESTORE DATABASE ... WITH REPLACE, RECOVERY`. |
| MongoDB    | `mongorestore --archive --drop`.                                                   |

For **file targets**, restore extracts the tar archive back to the original locations (or to an alternate directory with `--destination`), overwriting files with the same paths:

```bash
lutra restore --target app-config --file app-config_2026-02-08_033000_a1b2c3d4e5f6.tar.gz   # extracts to /
lutra restore --target app-config --destination /tmp/inspect --force                        # extracts elsewhere
```

### `lutra verify` (non-destructive)

Proves a backup is restorable without touching the production database. It checks the checksum sidecar, restores into a temporary database, runs a minimal validation query (counting tables/collections), and drops the temporary database afterwards. Results are recorded in `lutra history` as `verify` records.

```bash
lutra verify --target my-postgres            # Verifies the latest successful backup
```

Per database specifics:

- **PostgreSQL**: restores into a temporary `lutra_verify_<id>` database.
- **SQL Server**: reads `RESTORE FILELISTONLY` to build `MOVE` clauses for the temporary database name.
- **MongoDB**: remaps namespaces with `--nsFrom`/`--nsTo` into a temporary database. Uses `mongosh` when available, falling back to the legacy `mongo` shell in older images.
- **File targets**: reads through the archive to validate its integrity and counts the entries.

`verify` exits with code `0` on success and `1` on failure. Set `verify_schedule` on a database target and run `sudo lutra schedule install` to install a dedicated automated restore-drill timer.

### Advanced recovery boundaries

Lutra's default dump workflow intentionally remains simple. For PostgreSQL point-in-time recovery and WAL retention, use pgBackRest or WAL-G and document that repository in the rebuild runbook; copying a live `pg_wal` directory is not a valid backup. For SQL Server differential/log chains, use SQL Server Agent or a specialist maintenance solution until chain-aware restore is required. For MongoDB replica-set oplog consistency and sharded clusters, use MongoDB's supported coordinated backup tooling. Lutra's `config validate --preflight` confirms that each configured container and dump tool are available; test restores are the compatibility check that matters across database versions.

## Project Structure

```
Lutra/
├── src/
│   ├── Lutra.CLI/                          # Entry point + Spectre.Console commands
│   │   ├── Program.cs
│   │   ├── Commands/
│   │   │   ├── Backup/
│   │   │   │   ├── BackupRunCommand.cs     # Run backups (single or all)
│   │   │   │   └── BackupListCommand.cs    # List configured databases
│   │   │   ├── History/
│   │   │   │   └── HistoryCommand.cs       # Show backup history
│   │   │   ├── Restore/
│   │   │   │   └── RestoreCommand.cs       # Destructive restore into the configured database
│   │   │   ├── Verify/
│   │   │   │   └── VerifyCommand.cs        # Non-destructive test-restore verification
│   │   │   ├── Cleanup/
│   │   │   │   └── CleanupCommand.cs       # Trigger retention cleanup
│   │   │   ├── Config/
│   │   │   │   ├── ConfigInitCommand.cs    # Initialize config directories/files
│   │   │   │   ├── ConfigInitSettings.cs   # Settings for config init
│   │   │   │   ├── ConfigValidateCommand.cs # Validate configuration
│   │   │   │   ├── ConfigResetCommand.cs   # Reset config to template defaults
│   │   │   │   └── ConfigFileHelper.cs     # Shared path resolution helpers
│   │   │   ├── Schedule/
│   │   │   │   ├── ScheduleInstallCommand.cs # Install systemd timers
│   │   │   │   ├── ScheduleRemoveCommand.cs  # Remove systemd timers
│   │   │   │   └── ScheduleListCommand.cs    # List installed timers
│   │   │   ├── Uninstall/
│   │   │   │   ├── UninstallCommand.cs     # Remove all Lutra artifacts
│   │   │   │   └── UninstallSettings.cs    # Settings for uninstall
│   │   │   ├── GlobalSettings.cs           # Base CLI settings (--config, --env-file)
│   │   │   └── TargetSettings.cs           # Settings for target-specific commands
│   │   ├── Infrastructure/
│   │   │   └── ServiceFactory.cs           # Dependency creation
│   │   └── Lutra.CLI.csproj
│   │
│   └── Lutra.Core/                         # Core logic — no UI dependencies
│       ├── Configuration/
│       │   ├── BackupConfig.cs             # Root config model
│       │   ├── DatabaseTarget.cs           # Per-database config
│       │   ├── FileTarget.cs               # Per-file-target config
│       │   ├── RetentionPolicy.cs          # Retention rules
│       │   ├── DatabaseType.cs             # PostgreSql/SqlServer/MongoDb enum
│       │   ├── CompressionType.cs          # None/Gzip enum
│       │   ├── IConfigLoader.cs            # Config loader interface
│       │   ├── YamlConfigLoader.cs         # YAML config loading + validation
│       │   ├── ConfigTemplates.cs          # Default config/env file templates
│       │   └── ConfigurationException.cs   # Config errors
│       ├── Backup/
│       │   ├── IBackupProvider.cs          # Interface for DB-specific logic
│       │   ├── PostgresBackupProvider.cs   # PostgreSQL dump logic
│       │   ├── SqlServerBackupProvider.cs  # SQL Server backup logic
│       │   ├── MongoBackupProvider.cs      # MongoDB dump logic
│       │   ├── BackupOrchestrator.cs       # Coordinates backup workflow
│       │   ├── BackupResult.cs             # Result of a backup operation
│       │   ├── IProcessExecutor.cs         # Process execution interface
│       │   └── DockerProcessExecutor.cs    # Docker exec implementation
│       ├── Restore/
│       │   ├── IRestoreProvider.cs         # Interface for DB-specific restore logic
│       │   ├── PostgresRestoreProvider.cs  # PostgreSQL pg_restore/psql restore
│       │   ├── SqlServerRestoreProvider.cs # SQL Server .bak restore (+ FILELISTONLY/MOVE)
│       │   ├── MongoRestoreProvider.cs     # MongoDB mongorestore (namespace remap for tests)
│       │   └── RestoreOrchestrator.cs      # Coordinates restore and test-restore workflows
│       ├── Files/
│       │   ├── FileArchive.cs              # tar archive create/inspect/extract
│       │   └── GlobMatcher.cs              # Exclude pattern matching
│       ├── History/
│       │   ├── IBackupHistoryService.cs    # History service interface
│       │   ├── BackupHistoryService.cs     # Tracks backup metadata (JSON)
│       │   └── BackupRecord.cs             # Single backup entry
│       └── Lutra.Core.csproj
│
├── setup.sh                                # Automated installation script
├── lutra.example.yaml                      # Example configuration file
├── .env.example                            # Example environment file
├── Lutra.slnx                              # Solution file (XML format)
├── README.md
└── LICENSE.md
```

## Downloading Backups to a Local Machine

Lutra can push backups with its optional `sync` configuration. A pull from the Raspberry Pi or local machine provides stronger isolation and remains fully supported:

```bash
# One-time download
rsync -avz vps:/var/backups/lutra/ ~/backups/lutra/

# Automated via cron on your local machine (or Raspberry Pi)
# crontab -e
0 6 * * * rsync -avz vps:/var/backups/lutra/ ~/backups/lutra/

# Using scp (single target)
scp -r vps:/var/backups/lutra/my-postgres/ ~/backups/lutra/my-postgres/

# Using scp (all backups)
scp -r vps:/var/backups/lutra/ ~/backups/lutra/
```

## Security

- **No passwords in config files** — credentials are resolved from environment variables at runtime
- **No network exposure** — Lutra opens no ports and listens on no sockets
- **Docker socket access** — requires the user to be in the `docker` group or run as root
- **File permissions** — `lutra config validate` warns about overly permissive config and backup directories

## Requirements

- Linux (Ubuntu 22.04+, Debian 12+)
- Docker 20.10+
- Databases running in Docker containers

No .NET runtime needed — Lutra ships as a self-contained binary.

## Building from Source

```bash
git clone https://github.com/gustmrg/lutra.git
cd lutra

# Build self-contained binary
# Note: Trimming is disabled because Spectre.Console.Cli uses reflection
dotnet publish src/Lutra.CLI/Lutra.CLI.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained \
  -p:PublishSingleFile=true \
  -o dist/

# The binary is at dist/Lutra.CLI

# Install it
sudo cp dist/Lutra.CLI /usr/local/bin/lutra
sudo chmod +x /usr/local/bin/lutra

# Or use the automated setup script
./setup.sh
```

Run the automated test suite with:

```bash
dotnet test Lutra.slnx
```

CI runs unit tests, CLI smoke tests, and validates self-contained `linux-x64` and `linux-arm64` release archive layouts. Docker-based restore tests remain opt-in/manual because they require disposable database containers.

## Tech Stack

| Component       | Technology      |
|-----------------|-----------------|
| Runtime         | .NET 10.0 LTS   |
| TUI Framework   | Spectre.Console |
| Config Parsing  | YamlDotNet      |
| Scheduling      | systemd timers  |
| Backup Execution | `docker exec`   |
| History Storage | JSON file       |

## License

This project is licensed under the MIT License - see the [LICENSE.md](LICENSE.md) file for details.

Copyright (c) 2026 Gustavo Miranda

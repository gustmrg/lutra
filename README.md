# 🦦 Lutra

**Automated database backups for Docker containers.**

Lutra is a CLI tool that automates database backups for containerized databases running on a Linux VPS. Built with C# (.NET 10.0) and [Spectre.Console](https://spectreconsole.net/), it ships as a single self-contained binary — no runtime dependencies required.

It uses `docker exec` to run native dump tools (`pg_dump`, `mongodump`, `sqlcmd`) inside your containers, streams the output with optional gzip compression, and manages retention automatically. Scheduling is handled by systemd timers, not a custom daemon.

## Supported Databases

| Database   | Dump Tool    | Formats                          |
|------------|-------------|----------------------------------|
| PostgreSQL | `pg_dump`   | Custom (`.dump`), Plain (`.sql`) |
| SQL Server | `sqlcmd`    | Native backup (`.bak`)           |
| MongoDB    | `mongodump` | Archive (`.archive`)             |

## Quick Start

> **Note**: Lutra is in early development. Core backup functionality is implemented, but some features (like `restore` command) are still planned.

### Automated Setup (Recommended)

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
- Build Lutra from source
- Install the binary to `/usr/local/bin/lutra` (or `~/.local/bin/lutra`)
- Create configuration directories
- Generate template config and .env files
- Set proper permissions

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
    schedule: "0 3 * * *"
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

### Currently Implemented

```bash
# Backup commands
lutra backup run                          # Back up all configured databases
lutra backup run --target my-postgres     # Back up a specific database
lutra backup list                         # List configured databases and schedules

# History
lutra history                             # Show backup history for all targets
lutra history --target my-postgres        # Show history for specific target

# Maintenance
lutra cleanup                             # Remove old backups per retention policy
lutra cleanup --target my-postgres        # Clean up specific target

# Configuration
lutra config validate                     # Validate config file

# Scheduling
lutra schedule install                    # Generate and install systemd timers
lutra schedule install --target my-postgres  # Install timer for specific target
```

### Planned Features

The following commands are planned but not yet implemented:

```bash
lutra restore                             # Interactive restore (select DB → backup)
lutra cleanup --dry-run                   # Preview what would be deleted
```

### Global Options

All commands support these options:

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
| `retention.max_age_days` | integer | `30`                 | Delete backups older than N days      |

### Database Target Settings

| Property       | Type       | Default        | Description                                    |
|----------------|-----------|----------------|------------------------------------------------|
| `name`         | string    | —              | Friendly name (used in filenames and commands) |
| `type`         | enum      | —              | `postgresql`, `sqlserver`, or `mongodb`        |
| `container`    | string    | —              | Docker container name or ID                    |
| `database`     | string    | —              | Database name inside the container             |
| `username`     | string    | —              | Database user (required for PG and SQL Server) |
| `password_env` | string    | —              | Environment variable name holding the password |
| `schedule`     | cron expr | `"0 3 * * *"` | Cron expression for systemd timer generation   |
| `format`       | enum      | `custom`       | `custom` or `plain` (PostgreSQL only)          |
| `compression`  | enum      | `gzip`         | `gzip` or `none`                               |
| `retention`    | object    | global default | Override global retention for this target       |

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
    schedule: "0 3 * * *"
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
    schedule: "0 2 * * *"
    compression: gzip

  - name: app-mongo
    type: mongodb
    container: app-mongo
    database: app_data
    schedule: "0 4 * * 0"  # Weekly on Sundays
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
         │ rsync/scp (external, not managed by Lutra)
         ▼
   Local Machine / Raspberry Pi
```

### Backup File Structure

```
/var/backups/lutra/
├── backup-history.json
├── example-db/
│   ├── example-db_2026-02-08_030000.dump.gz
│   ├── example-db_2026-02-07_030000.dump.gz
│   └── example-db_2026-02-06_030000.dump.gz
├── finance-db/
│   ├── finance-db_2026-02-08_020000.bak.gz
│   └── finance-db_2026-02-07_020000.bak.gz
└── app-mongo/
    └── app-mongo_2026-02-02_040000.archive.gz
```

### Retention Policy

Backups are deleted only when **both** conditions are met (conservative approach):
- The backup count exceeds `max_count` for that target
- The backup age exceeds `max_age_days`

Per-database retention settings override global defaults.

## Project Structure

```
Lutra/
├── src/
│   ├── Lutra.CLI/                        # Entry point + Spectre.Console commands
│   │   ├── Program.cs
│   │   ├── Commands/
│   │   │   ├── Backup/
│   │   │   │   ├── BackupRunCommand.cs   # Run backups (single or all)
│   │   │   │   └── BackupListCommand.cs  # List configured databases
│   │   │   ├── History/
│   │   │   │   └── HistoryCommand.cs     # Show backup history
│   │   │   ├── Cleanup/
│   │   │   │   └── CleanupCommand.cs     # Trigger retention cleanup
│   │   │   ├── Config/
│   │   │   │   └── ConfigValidateCommand.cs # Validate configuration
│   │   │   ├── Schedule/
│   │   │   │   └── ScheduleInstallCommand.cs # Install systemd timers
│   │   │   ├── GlobalSettings.cs         # Base CLI settings
│   │   │   └── TargetSettings.cs         # Settings for target-specific commands
│   │   ├── Infrastructure/
│   │   │   └── ServiceFactory.cs         # Dependency creation
│   │   └── Lutra.CLI.csproj
│   │
│   └── Lutra.Core/                       # Core logic — no UI dependencies
│       ├── Configuration/
│       │   ├── BackupConfig.cs           # Root config model
│       │   ├── DatabaseTarget.cs         # Per-database config
│       │   ├── RetentionPolicy.cs        # Retention rules
│       │   ├── ConfigurationLoader.cs    # YAML loading + validation
│       │   └── ConfigurationException.cs # Config errors
│       ├── Backup/
│       │   ├── IBackupProvider.cs        # Interface for DB-specific logic
│       │   ├── PostgresBackupProvider.cs # PostgreSQL dump logic
│       │   ├── SqlServerBackupProvider.cs # SQL Server backup logic
│       │   ├── MongoBackupProvider.cs    # MongoDB dump logic
│       │   ├── BackupOrchestrator.cs     # Coordinates backup workflow
│       │   ├── BackupResult.cs           # Result of a backup operation
│       │   └── DockerExecCommand.cs      # Docker exec command model
│       ├── History/
│       │   ├── BackupHistoryService.cs   # Tracks backup metadata (JSON)
│       │   └── BackupRecord.cs           # Single backup entry
│       └── Lutra.Core.csproj
│
├── setup.sh                              # Automated installation script
├── CLAUDE.md                             # Developer guidance
├── Lutra.slnx                            # Solution file (XML format)
├── README.md
└── LICENSE
```

## Downloading Backups to a Local Machine

Lutra intentionally does **not** manage offsite transfers. Backup creation and backup transfer are separate concerns. Use standard tools to pull backups from your VPS:

```bash
# One-time download
rsync -avz vps:/var/backups/lutra/ ~/backups/lutra/

# Automated via cron on your local machine (or Raspberry Pi)
# crontab -e
0 6 * * * rsync -avz vps:/var/backups/lutra/ ~/backups/lutra/
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

# Lutra

**Automated database and configuration backups for a Docker-based VPS.**

Lutra is a Linux CLI that runs native database dump tools inside Docker containers, archives selected files and volumes, and manages checksums, manifests, retention, restore verification, optional encryption, and offsite copies. It is distributed as a self-contained binary, so no .NET runtime is required on the server.

> Lutra is in early development. Core backup, restore, and restore verification functionality is implemented.

## Supported Databases

| Database | Tool | Formats |
|---|---|---|
| PostgreSQL | `pg_dump` | Custom (`.dump`) or plain (`.sql`) |
| SQL Server | `sqlcmd` | Native backup (`.bak`) |
| MongoDB | `mongodump` | Archive (`.archive`) |
| SQLite | `sqlite3` | Consistent online copy (`.sqlite`) |

## Quick Start

### 1. Install the latest binary

The recommended installation downloads the setup script and executes it in release-binary mode. It automatically selects `linux-x64` or `linux-arm64`, verifies the SHA-256 checksum, installs Lutra, and creates configuration templates.

For a VPS that should run scheduled backups, choose a system-wide installation. Choose a user-only installation when you do not have root access or are evaluating Lutra. Both modes use the same binary and features; only the installation paths and required permissions differ.

For a system-wide installation:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | sudo bash -s
```

For a user-only installation, omit `sudo`:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | bash -s
```

The setup script creates `/etc/lutra` and `/var/backups/lutra` for system installs, or `~/.config/lutra` and `~/backups/lutra` for user installs. See [Installation](docs/installation.md) for prerequisites, manual binary installation, updating, and uninstalling.

### 2. Configure targets and credentials

Edit `lutra.yaml` to describe your containers and targets. Put passwords in `.env`, not in the YAML file.

```bash
# System-wide installation
sudo nano /etc/lutra/lutra.yaml
sudo nano /etc/lutra/.env

# User-only installation
nano ~/.config/lutra/lutra.yaml
nano ~/.config/lutra/.env
```

Use [`lutra.example.yaml`](lutra.example.yaml) and [`.env.example`](.env.example) as starting points. The full reference is in [Configuration](docs/configuration.md).

### 3. Validate and run

```bash
lutra config validate --preflight
lutra backup run
sudo lutra schedule install
```

See [Commands](docs/commands.md) for the complete CLI reference and [Operations](docs/operations.md) for restore, verification, retention, bundles, and offsite copies.

## Documentation

- [Installation](docs/installation.md): binary installation, configuration paths, updates, and uninstalling
- [Commands](docs/commands.md): complete command reference and global options
- [Configuration](docs/configuration.md): YAML settings, targets, encryption, sync, notifications, and schedules
- [Operations](docs/operations.md): backup layout, retention, restore, verify, disaster recovery, and offsite copies
- [Security](docs/security.md): credential handling, encryption, permissions, and isolation guidance
- [Development](docs/development.md): source builds, tests, project structure, and release requirements

## Requirements

- Linux (Ubuntu 22.04+ or Debian 12+)
- Docker 20.10+ for database or volume targets
- Database dump tools installed in the relevant containers

Lutra does not require a .NET runtime when installed from a release binary.

## License

This project is licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.

Copyright (c) 2026 Gustavo Miranda

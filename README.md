# Lutra

**Automated PostgreSQL backups for a Docker-based VPS.**

Lutra is a Linux CLI that runs `pg_dump` inside Docker containers and manages checksums, manifests, retention, restore verification, and offsite copies. It is distributed as a self-contained binary, so no .NET runtime is required on the server.

> Lutra is in early development. Core backup, restore, and restore verification functionality is implemented.

## Supported Databases

Lutra backs up PostgreSQL with `pg_dump`, in custom (`.dump`) or plain (`.sql`) format.

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

The setup script creates `/etc/lutra`, `/var/backups/lutra`, and `/var/lib/lutra` for system installs. User installs use `~/.config/lutra`, `~/backups/lutra`, and `$XDG_STATE_HOME/lutra` (or `~/.local/state/lutra`). See [Installation](docs/installation.md) for prerequisites, ownership, updating, and uninstalling.

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

See [Commands](docs/commands.md) for the complete CLI reference and [Operations](docs/operations.md) for restore, verification, retention, and offsite copies.

## Documentation

- [Installation](docs/installation.md): binary installation, configuration paths, updates, and uninstalling
- [Commands](docs/commands.md): complete command reference and global options
- [Configuration](docs/configuration.md): YAML settings, targets, sync, notifications, and schedules
- [Operations](docs/operations.md): backup layout, retention, restore, verify, and offsite copies
- [Security](docs/security.md): credential handling, permissions, and isolation guidance
- [Development](docs/development.md): source builds, tests, project structure, and release requirements

## Requirements

- Linux (Ubuntu 22.04+ or Debian 12+)
- Docker 20.10+
- `pg_dump` and `psql` available in the PostgreSQL containers

Lutra does not require a .NET runtime when installed from a release binary.

## License

This project is licensed under the MIT License. See [LICENSE.md](LICENSE.md) for details.

Copyright (c) 2026 Gustavo Miranda

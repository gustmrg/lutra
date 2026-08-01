# Installation

Lutra runs on Linux and is distributed as a self-contained binary. The normal installation path downloads the setup script, then lets the script download and verify the latest release binary.

## Prerequisites

- Linux (Ubuntu 22.04+ or Debian 12+)
- Docker 20.10+
- Permission to run Docker commands

The database tools (`pg_dump`, `sqlcmd`, `mongodump`, or `sqlite3`) must be installed in the containers that contain the databases. Lutra does not require a .NET runtime or SDK for binary installations.

## Recommended Installation

Download and execute the setup script. It installs the latest pre-built binary by default, even if the .NET SDK happens to be installed on the server.

For a VPS that should run scheduled backups, use a system-wide installation. It is available to all users, works naturally with systemd, and uses `/usr/local/bin`, `/etc/lutra`, and `/var/backups/lutra`. Use a user-only installation when you do not have root access or are evaluating Lutra. It uses `~/.local/bin`, `~/.config/lutra`, and `~/backups/lutra`. The binary and features are otherwise the same.

For a system-wide installation:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | sudo bash -s
```

This installs the binary at `/usr/local/bin/lutra`, creates `/etc/lutra`, and stores backups under `/var/backups/lutra`.

For a user-only installation:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | bash -s
```

This installs the binary at `~/.local/bin/lutra`, creates `~/.config/lutra`, and adds `~/.local/bin` to your shell's `PATH` when needed.

The setup script also:

- Detects Docker and warns when the current user cannot run it.
- Downloads the matching `linux-x64` or `linux-arm64` release archive.
- Verifies the archive's SHA-256 checksum.
- Creates `lutra.yaml` and `.env` templates without overwriting existing files.

## Configure Lutra

Edit the generated files:

```bash
# System-wide installation
sudo nano /etc/lutra/lutra.yaml
sudo nano /etc/lutra/.env

# User-only installation
nano ~/.config/lutra/lutra.yaml
nano ~/.config/lutra/.env
```

Set each target's container, database, and schedule. Put passwords in `.env`, not in YAML, and keep the file mode at `600`.

See [Configuration](configuration.md) and the checked-in [`lutra.example.yaml`](../lutra.example.yaml) and [`.env.example`](../.env.example) files for complete examples.

## First Backup

Validate the configuration before running a backup:

```bash
lutra config validate
lutra config validate --preflight
lutra backup run
```

Install systemd timers when the configuration is ready:

```bash
sudo lutra schedule install
lutra schedule list
```

## Manual Binary Installation

The setup script is recommended because it selects the architecture and verifies the download. To install the archive manually instead:

```bash
RID=linux-x64 # use linux-arm64 on a 64-bit ARM server
curl -fLO "https://github.com/gustmrg/lutra/releases/latest/download/lutra-${RID}.tar.gz"
curl -fLO "https://github.com/gustmrg/lutra/releases/latest/download/lutra-${RID}.tar.gz.sha256"
sha256sum -c "lutra-${RID}.tar.gz.sha256"
tar -xzf "lutra-${RID}.tar.gz"
sudo install -m 0755 lutra /usr/local/bin/lutra
```

You must create the configuration and backup directories yourself when using this method.

## Building From Source

Source builds are an exception for contributors and development environments. Clone the repository, install the .NET 10 SDK, and run:

```bash
./setup.sh --from-source
```

The source checkout and SDK are required for this mode. The older `--from-release` option remains available as an explicit alias for the default release-binary mode.

## Updating

Run the same setup command again. Existing configuration and environment files are preserved, and the latest release binary replaces the installed binary.

## Uninstalling

```bash
sudo lutra uninstall
```

The uninstall command removes Lutra's installed binary, configuration, backup data, and systemd timer artifacts according to its confirmation prompts. Export anything you need before using it.

# Installation

Lutra runs on Linux and is distributed as a self-contained binary. The normal installation path downloads the setup script, then lets the script download and verify the latest release binary.

## Prerequisites

- Linux (Ubuntu 22.04+ or Debian 12+)
- Docker 20.10+ for database or volume targets
- Permission to run Docker commands for database or volume targets

The database tools (`pg_dump`, `sqlcmd`, `mongodump`, or `sqlite3`) must be installed in the containers that contain the databases. File targets run on the host and do not require Docker. Lutra does not require a .NET runtime or SDK for binary installations.

## Recommended Installation

Download and execute the setup script. It installs the latest pre-built binary by default, even if the .NET SDK happens to be installed on the server.

For a VPS that should run scheduled backups, use a system-wide installation. It is available to all users, works naturally with systemd, and uses `/usr/local/bin`, `/etc/lutra`, `/var/backups/lutra`, and `/var/lib/lutra`. Use a user-only installation when you do not have root access or are evaluating Lutra. It uses `~/.local/bin`, `~/.config/lutra`, `~/backups/lutra`, and `$XDG_STATE_HOME/lutra` (or `~/.local/state/lutra`). The binary and features are otherwise the same.

For a system-wide installation:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | sudo bash -s
```

This installs the binary at `/usr/local/bin/lutra`, creates `/etc/lutra`, stores backups under `/var/backups/lutra`, and creates local application state under `/var/lib/lutra`.

For a user-only installation:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | bash -s
```

This installs the binary at `~/.local/bin/lutra`, creates `~/.config/lutra` and the resolved user state directory, and adds `~/.local/bin` to your shell's `PATH` when needed.

The setup script also:

- Detects Docker and warns when the current user cannot run it.
- Downloads the matching `linux-x64` or `linux-arm64` release archive.
- Verifies the archive's SHA-256 checksum.
- Creates `lutra.yaml` and `.env` templates without overwriting existing files.
- Writes an explicit `state_directory` and creates it with ownership for the account that will run Lutra. Scheduled and manual commands must use that same account; setup does not change systemd service identity.

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

You must create the configuration, backup, and local state directories yourself when using this method. Ensure the same OS account that runs Lutra can create the SQLite DB, WAL, and SHM files in the state directory.

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
sudo lutra uninstall --keep-state
sudo lutra uninstall --keep-backups
```

Interactive uninstall displays and prompts for the resolved state directory separately from backup data. `--keep-state` preserves local history. `--keep-backups` preserves both backup artifacts and state. With `--yes`, state is deleted only when neither preservation flag is present. Export anything you need before using destructive options.

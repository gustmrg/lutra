# Installation

Lutra runs on Linux and is distributed as a self-contained binary. The normal installation path downloads the setup script, then lets the script download and verify the latest release binary.

## Prerequisites

- Linux (Ubuntu 22.04+ or Debian 12+)
- Docker 20.10+ for database or volume targets
- Permission to run Docker commands for database or volume targets

The database tools (`pg_dump`, `sqlcmd`, `mongodump`, or `sqlite3`) must be installed in the containers that contain the databases. File targets run on the host and do not require Docker. Lutra does not require a .NET runtime or SDK for binary installations.

## Recommended Installation

Download and execute the setup script. It installs the latest pre-built binary by default, even if the .NET SDK happens to be installed on the server.

Lutra supports a single system-wide installation layout: `/usr/local/bin`, `/etc/lutra`, `/var/backups/lutra`, and `/var/lib/lutra`. Installation scope does not determine runtime privilege. The binary and systemd units are managed as root, while manual and scheduled Lutra operations run as one explicitly selected non-root account.

Run setup through `sudo` from the account that should operate Lutra:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | sudo bash -s
```

Setup uses `SUDO_USER` as the runtime account. When running from a root shell or automation, pass it explicitly:

```bash
curl -fsSL https://raw.githubusercontent.com/gustmrg/lutra/main/setup.sh | sudo bash -s -- --run-as gustavo
```

This installs the root-owned executable at `/usr/local/bin/lutra`. The selected runtime account owns `/etc/lutra`, `/var/backups/lutra`, and `/var/lib/lutra`, so it can edit configuration and consistently create backup artifacts, SQLite state, locks, and sync markers. User-local installation is not supported.

The setup script also:

- Detects Docker and warns when the current user cannot run it.
- Downloads the matching `linux-x64` or `linux-arm64` release archive.
- Verifies the archive's SHA-256 checksum.
- Creates `lutra.yaml` and `.env` templates without overwriting existing files.
- Writes an explicit `state_directory` and assigns configuration, backups, and state to the selected runtime account.
- Checks Docker access as the runtime account rather than as root.

## Configure Lutra

Edit the generated files:

```bash
nano /etc/lutra/lutra.yaml
nano /etc/lutra/.env
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
sudo lutra schedule install --run-as gustavo
lutra schedule list
```

`schedule install` requires `--run-as` and rejects root. Every generated service records `User=gustavo` and the account's home directory, so scheduled backups and post-backup sync use the same filesystem and SSH identity as manual commands. Replace `gustavo` with the account selected during setup.

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

You must create `/etc/lutra`, `/var/backups/lutra`, and `/var/lib/lutra` yourself when using this method and assign them to one non-root runtime account. Ensure that account can read the configuration and create the SQLite DB, WAL, and SHM files in the state directory.

## Building From Source

Source builds are an exception for contributors and development environments. Clone the repository, install the .NET 10 SDK, and run:

```bash
sudo ./setup.sh --from-source --run-as gustavo
```

The source checkout and SDK are required for this mode. The older `--from-release` option remains available as an explicit alias for the default release-binary mode.

## Updating

Run the same setup command again. Existing configuration and environment files are preserved, and the latest release binary replaces the installed binary.

After an update that changes scheduling behavior, regenerate all units with the selected runtime account and reload systemd:

```bash
sudo lutra schedule install --run-as gustavo
sudo systemctl daemon-reload
```

This also replaces older Lutra services that omitted `User=` and therefore ran as root.

## Uninstalling

```bash
sudo lutra uninstall
sudo lutra uninstall --keep-state
sudo lutra uninstall --keep-backups
```

Interactive uninstall displays and prompts for the resolved state directory separately from backup data. `--keep-state` preserves local history. `--keep-backups` preserves both backup artifacts and state. With `--yes`, state is deleted only when neither preservation flag is present. Export anything you need before using destructive options.

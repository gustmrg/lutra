# Configuration

Lutra reads a YAML configuration file and a separate environment file. The system-wide defaults are `/etc/lutra/lutra.yaml` and `/etc/lutra/.env`; user installations use `~/.config/lutra/lutra.yaml` and `~/.config/lutra/.env`. Unless `--env-file` is provided, Lutra uses the `.env` file beside the selected YAML configuration.

Use `lutra config validate --preflight` to validate the YAML and check systemd, Docker, containers, and dump-tool availability.

## Global Settings

| Property | Type | Default | Description |
|---|---|---|---|
| `backup_directory` | string | required | Base directory for backups |
| `retention.max_count` | integer | `10` | Maximum backups per target |
| `retention.max_age_days` | integer | `30` | Backup age threshold |
| `retention.mode` | enum | `both` | Delete when `both` or `either` limit matches |
| `retention.keep_at_least` | integer | `1` | Always preserve this many newest backups |
| `notifications.discord.webhooks` | list | `[]` | Discord incoming webhooks referenced through environment variables |
| `state_directory` | path | installation-specific | Local application database directory; never backup content |
| `sync` | object | unset | Optional SSH/rsync offsite destination |

See [`lutra.example.yaml`](../lutra.example.yaml) for a complete annotated configuration.

New installations always write an explicit absolute `state_directory`. System installs use `/var/lib/lutra`; user installs use `$XDG_STATE_HOME/lutra` when `XDG_STATE_HOME` is absolute, otherwise `~/.local/state/lutra`. A legacy config under `/etc/lutra` that omits it resolves to `/var/lib/lutra`. Any other custom/legacy config resolves to `<backup_directory>/.lutra-state` for compatibility, and `config validate` warns until the path is made explicit.

One state directory belongs to one normalized configuration path. Sharing it between unrelated custom configs is refused; choose a separate `state_directory` for each installation. `config validate` reports the resolved path, opens the database in WAL mode, and performs a rolled-back write probe. The executing account must be able to create and open `lutra.db`, `lutra.db-wal`, and `lutra.db-shm` there.

## Database Targets

| Property | Type | Default | Description |
|---|---|---|---|
| `name` | string | required | Friendly name used in filenames and commands |
| `type` | enum | required | `postgresql` |
| `container` | string | required | Docker container name or ID |
| `database` | string | required | Database name inside the container |
| `username` | string | required | Database user |
| `password_env` | string | unset | Environment variable containing the password; in `.env`, surrounding quotes are removed |
| `schedule` | string | `*-*-* 03:00:00` | systemd calendar expression |
| `verify_schedule` | string | unset | Optional automated restore-drill schedule |
| `format` | enum | `custom` | `pg_dump` format: `custom` or `plain` |
| `compression` | enum | `gzip` | `gzip` or `none` |
| `retention` | object | global | Per-target retention override |

## Sync and Notifications

Configure offsite sync with `sync.type: rsync`, `host`, `user`, `destination_path`, and `ssh_key_path`. Optional fields are `port`, `extra_args`, `post_backup`, and `delete`. Remote deletion is disabled unless configured or explicitly requested with `--delete`. Use `--dry-run` before the first transfer. Successful transfers write a `.last-sync.json` marker next to the synced files. `lutra sync --validate` needs rsync 3.2.3 or later on both hosts, and creates `destination_path` if it is missing. To limit the destination key to rsync, see [Restricting the Destination Key](operations.md#restricting-the-destination-key).

Discord notifications are optional, synchronous, and best-effort. They never change
an operation's exit status. Backup messages include one success or failure embed per
target with the artifact name, size, destination, duration, error, and container
where applicable. Sync, verification, and restore commands send summary
embeds. Large backup runs are split across multiple Discord messages.

Create an incoming webhook from the Discord channel's **Edit Channel > Integrations
> Webhooks** screen. Store the complete webhook URL in Lutra's `.env` file because
the URL contains a credential:

```dotenv
LUTRA_DISCORD_WEBHOOK=https://discord.com/api/webhooks/...
```

Reference the environment variable from `lutra.yaml`:

```yaml
notifications:
  discord:
    webhooks:
      - url_env: LUTRA_DISCORD_WEBHOOK
```

Add more `url_env` entries to send every notification to multiple Discord channels.
Each variable must resolve to an HTTPS URL hosted by Discord. Delivery uses a
10-second HTTP timeout, does not retry, suppresses Discord mentions, and writes
sanitized failures to stderr without exposing the webhook URL.

The former `notifications.webhooks` and `notifications.healthchecks_url` settings
are no longer supported. Core backups, local history, and systemd status/logging
do not require notifications or network access.

## Schedules

Schedules use [systemd `OnCalendar` expressions](https://www.freedesktop.org/software/systemd/man/latest/systemd.time.html):

```yaml
schedule: "*-*-* 03:00:00"       # Daily at 3 AM
# schedule: "*-*-* 00/6:00:00"    # Every 6 hours
# schedule: "Sun *-*-* 02:00:00"  # Sundays at 2 AM
```

Validate an expression with:

```bash
systemd-analyze calendar "*-*-* 03:00:00"
```

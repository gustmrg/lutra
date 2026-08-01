# Configuration

Lutra reads a YAML configuration file and a separate environment file. The system-wide defaults are `/etc/lutra/lutra.yaml` and `/etc/lutra/.env`; user installations use `~/.config/lutra/lutra.yaml` and `~/.config/lutra/.env`.

Use `lutra config validate --preflight` to validate the YAML and check systemd, Docker, containers, and dump-tool availability.

## Global Settings

| Property | Type | Default | Description |
|---|---|---|---|
| `backup_directory` | string | required | Base directory for backups |
| `retention.max_count` | integer | `10` | Maximum backups per target |
| `retention.max_age_days` | integer | `30` | Backup age threshold |
| `retention.mode` | enum | `both` | Delete when `both` or `either` limit matches |
| `retention.keep_at_least` | integer | `1` | Always preserve this many newest backups |
| `notifications.webhooks` | list | `[]` | JSON webhook endpoints for operation and health events |
| `notifications.healthchecks_url` | string | unset | Healthchecks.io-compatible ping URL |
| `sync` | object | unset | Optional SSH/rsync offsite destination |
| `encryption` | object | unset | Global age recipient inherited by targets |

See [`lutra.example.yaml`](../lutra.example.yaml) for a complete annotated configuration.

## Database Targets

| Property | Type | Default | Description |
|---|---|---|---|
| `name` | string | required | Friendly name used in filenames and commands |
| `type` | enum | required | `postgresql`, `sqlserver`, `mongodb`, or `sqlite` |
| `container` | string | required | Docker container name or ID |
| `database` | string | required | Database name, or SQLite file path inside the container |
| `username` | string | unset | Database user, required for PostgreSQL and SQL Server |
| `password_env` | string | unset | Environment variable containing the password |
| `schedule` | string | `*-*-* 03:00:00` | systemd calendar expression |
| `verify_schedule` | string | unset | Optional automated restore-drill schedule |
| `postgres_wal_archive_path` | string | unset | Host directory populated by PostgreSQL `archive_command` |
| `sql_server_backup_kind` | enum | `full` | SQL Server `full`, `differential`, or `log` |
| `mongo_oplog` | boolean | `false` | Replica-set-wide `mongodump --oplog` archive |
| `format` | enum | `custom` | PostgreSQL `custom` or `plain` |
| `compression` | enum | `gzip` | `gzip` or `none` |
| `retention` | object | global | Per-target retention override |

SQLite uses the `.backup` command for an online-consistent copy. The `sqlite3` binary must be installed in the container. Stop the application container before a destructive SQLite restore.

## Docker Volumes

Named volumes are configured under `volumes`. Lutra uses a temporary Alpine container to create a read-only tar archive:

```yaml
volumes:
  - name: app-uploads
    volume: app_uploads
    schedule: "*-*-* 03:15:00"
    compression: gzip
```

Restoring a volume deletes its existing contents. Stop all containers that use it first.

## File Targets

File targets archive compose files, `.env` files, reverse proxy configuration, certificates, and other selected paths. Do not use them for system state such as installed packages, users, or firewall rules.

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

Directories are archived recursively. Exclude patterns support `*` and `?` and can match a single path segment. Paths are stored relative to the filesystem root and restored to their original locations unless `--destination` is used.

## Server Inventory

Inventory snapshots are rebuild aids, not backups of system state:

```yaml
inventory:
  enabled: true
  schedule: "*-*-* 04:00:00"
  collectors: [docker, packages, systemd, crontabs, firewall]
```

Omit `collectors` to run all collectors. Missing tools and collector failures are recorded in the snapshot without failing database or file backups. Docker environment names are recorded but values are omitted, and cron commands are omitted to avoid capturing credentials.

## Encryption

Backups can be encrypted after compression with [age](https://age-encryption.org). Store only the public recipient in Lutra configuration; keep the private identity key off the VPS:

```yaml
encryption:
  type: age
  recipient: age1...
```

Checksums cover the encrypted `.age` artifact, and manifests contain only a recipient fingerprint. Decrypt on a trusted host before restoring:

```bash
age --decrypt --identity /secure/path/lutra.agekey \
  --output app-config.tar.gz app-config.tar.gz.age
lutra restore --target app-config --file app-config.tar.gz
```

`config validate --preflight` checks that `age` is installed. Sensitive file paths produce warnings when neither global nor target encryption is configured.

## Sync and Notifications

Configure offsite sync with `sync.type: rsync`, `host`, `user`, `destination_path`, and `ssh_key_path`. Optional fields are `port`, `extra_args`, `post_backup`, and `delete`. Remote deletion is disabled unless configured or explicitly requested with `--delete`. Use `--dry-run` before the first transfer. Successful transfers write a `.last-sync.json` marker used by health checks.

Notifications are best-effort and never change an operation's exit status. Generic webhooks receive event, status, summary, target, timestamp, and host fields. Healthchecks.io receives a direct success ping or a `/fail` ping for failures.

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

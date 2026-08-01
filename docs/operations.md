# Operations

## How It Works

Lutra runs on the VPS alongside your containers. It does not connect to databases over the network. It executes native dump commands inside containers with `docker exec`, streams output to disk, optionally compresses it, and records results in local JSON history. systemd timers handle scheduling.

Supported database providers are:

| Database | Tool | Formats |
|---|---|---|
| PostgreSQL | `pg_dump` | Custom (`.dump`) or plain (`.sql`) |
| SQL Server | `sqlcmd` | Native backup (`.bak`) |
| MongoDB | `mongodump` | Archive (`.archive`) |
| SQLite | `sqlite3` | Consistent online copy (`.sqlite`) |

## Backup Layout

```text
/var/backups/lutra/
├── backup-history.json
├── inventory/
│   ├── inventory_2026-02-08_040000_a1b2c3d4e5f6.md
│   └── inventory_2026-02-08_040000_a1b2c3d4e5f6.md.sha256
├── example-db/
│   ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz
│   ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz.sha256
│   └── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz.json
└── app-mongo/
    └── app-mongo_2026-02-02_040000_a1b2c3d4e5f6.archive.gz
```

Successful backups write two integrity sidecars:

- `.sha256` stores the backup file's SHA-256 checksum.
- `.json` stores target metadata, size, checksum, duration, format, compression, and Lutra version.

Use `lutra backup verify-file --file <PATH>` to verify an artifact. Use `lutra backup reconcile` to compare target directories with successful history entries. It reports untracked files, missing files, and missing sidecars; exit code `1` means inconsistencies were found.

## Retention

The default `mode: both` is conservative: a backup is deleted only when both its count and age limits are exceeded. Set `mode: either` to delete when either limit matches. `keep_at_least` always protects the newest successful backups. Per-target settings override global defaults.

`cleanup --dry-run` previews every affected path. Orphan sidecars require `--orphan-sidecars`. Untracked backup files require `--orphan-files` plus confirmation or `--force`. `--prune-history` removes old failure, verify, and sync records while preserving successful backup history.

`lutra health` checks backup age, failure streaks, size and duration anomalies, missing history-referenced files, and checksum or sidecar issues. It exits `0` when healthy, `1` for warnings, and `2` for critical findings. Use `--json` for monitoring integrations.

## Restore

`lutra restore` is destructive and replaces the configured target contents. It asks for confirmation unless `--force` is passed. Omitting `--target` or `--file` opens interactive selection prompts.

```bash
lutra restore --target my-postgres --file /var/backups/lutra/my-postgres/latest.dump.gz

# Restore a SQL Server full/differential/log chain in order.
lutra restore --target finance-db --force \
  --chain full.bak.gz --chain latest.diff.bak.gz \
  --chain log-001.log.bak.gz --chain log-002.log.bak.gz
```

Restore mechanisms are:

- PostgreSQL: `pg_restore --clean --if-exists` for custom format; plain format drops and recreates the database, then loads with `psql`.
- SQL Server: streams the `.bak` into the container and runs `RESTORE DATABASE ... WITH REPLACE, RECOVERY`.
- MongoDB: runs `mongorestore --archive --drop`.
- SQLite: streams the consistent copy over the configured database file; stop the application first.
- File targets: extracts the tar archive to the original paths, or to an alternate `--destination`.

## Verify

`lutra verify` proves a backup is restorable without touching the production database. It checks the checksum, restores into a temporary database where supported, runs a minimal validation query, and removes the temporary database. Results are recorded as `verify` records in history.

```bash
lutra verify --target my-postgres
```

PostgreSQL uses a temporary `lutra_verify_<id>` database. SQL Server builds `MOVE` clauses from `RESTORE FILELISTONLY`. MongoDB remaps namespaces with `--nsFrom` and `--nsTo`, using `mongosh` or the legacy `mongo` shell. File targets are read through and their entries counted. Verification exits `0` on success and `1` on failure.

Set `verify_schedule` on a database target and run `sudo lutra schedule install` to schedule restore drills.

## Advanced Recovery

- PostgreSQL WAL/PITR: configure `archive_command` to copy completed WAL segments to a host directory and set `postgres_wal_archive_path`. Never point it at live `pg_wal`; pgBackRest or WAL-G remains preferable for full PITR orchestration and repository pruning.
- SQL Server chains: set `sql_server_backup_kind` to `full`, `differential`, or `log`. Restore an ordered chain with repeated `--chain`; Lutra validates checksums and ordering.
- MongoDB oplog: set `mongo_oplog: true` for a replica-set-wide archive restored with `--oplogReplay`. Namespace-remapped test restores are rejected; verify these in a disposable replica set. Sharded clusters require MongoDB's coordinated backup tooling.

`config validate --preflight` reports installed dump-tool versions. Scheduled disposable restore drills are the strongest practical cross-version compatibility check.

## Disaster Recovery Bundles

`lutra bundle` creates a checksum-protected archive containing the latest successful artifact and sidecars for every configured target, the latest inventory snapshot, a copy of `lutra.yaml`, environment variable names without values, and generated `RESTORE.md`. Bundle creation fails rather than silently omitting a target whose backup is missing.

Use `--encrypt` to protect the complete archive with the global age recipient. Bundles are written under `<backup_directory>/bundles/` and included in a full `lutra sync`. The generated instructions identify system state Lutra does not cover.

## Offsite Copies

Lutra can push backups with its optional rsync configuration. A pull from a Raspberry Pi or local machine provides stronger isolation and is also supported:

```bash
rsync -avz vps:/var/backups/lutra/ ~/backups/lutra/

# Example local cron entry
0 6 * * * rsync -avz vps:/var/backups/lutra/ ~/backups/lutra/

scp -r vps:/var/backups/lutra/my-postgres/ ~/backups/lutra/my-postgres/
```

For a Raspberry Pi, use a dedicated restricted user and grant its SSH key write access only to the destination directory. A pull-based timer is safer because a compromised VPS cannot use the Pi's credentials to delete the repository. Restic, Borg, and Kopia can point at Lutra's `backup_directory` instead of using built-in sync.

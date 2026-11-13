# Operations

## How It Works

Lutra runs on the VPS alongside your containers. It does not connect to databases over the network. It runs `pg_dump` inside the container with `docker exec`, streams the output to disk, optionally compresses it, and records the result in the local `lutra.db` application database. systemd timers handle scheduling.

Dumps use the PostgreSQL custom format (`.dump`) by default, or plain SQL (`.sql`) with `format: plain`.

## Backup Layout

```text
/var/backups/lutra/
└── example-db/
    ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz
    ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz.sha256
    ├── example-db_2026-02-08_030000_a1b2c3d4e5f6.dump.gz.json
    └── .last-sync.json
```

Successful backups write two integrity sidecars:

- `.sha256` stores the backup file's SHA-256 checksum.
- `.json` stores target metadata, size, checksum, duration, format, compression, and Lutra version.

Application state is stored separately at `<state_directory>/lutra.db` (normally
`/var/lib/lutra/lutra.db` for a system install). Custom configurations without
`state_directory` use `<backup_directory>/.lutra-state/lutra.db` for compatibility.

`lutra.db`, `lutra.db-wal`, and `lutra.db-shm` are local operational state, not backup artifacts. They must remain on a local filesystem and be accessed by the same OS account for manual and scheduled commands. NFS and multi-host shared state directories are unsupported because WAL requires local coordination. Independent custom configurations must use distinct `state_directory` values.

History rows are created before work starts. Backup, verify, and sync operations therefore appear as `Creating`, `Verifying`, or `Uploading`, then transition to `Succeeded`, `Failed`, or `Cancelled`. A process that dies leaves a lease; after expiry, the next history access marks it `Interrupted`. Interrupted means completion was not confirmed, but a finalized backup artifact is retained for reconciliation.

Losing the state directory loses local history and in-flight operation state. It does not invalidate or delete backup artifacts, manifests, or checksums. `lutra backup reconcile` can identify artifacts without records, but automatic history reconstruction is not currently supported.

Installations that predate the SQLite history may still have a `backup-history.json` in the backup directory. Lutra no longer reads it and sync excludes it; it can be deleted.

Use `lutra backup verify-file --file <PATH>` to verify an artifact against its sidecars. Use `lutra backup reconcile` to compare target directories with successful history entries. It reports untracked files, missing files, and missing sidecars; exit code `1` means inconsistencies were found.

## Retention

The default `mode: both` is conservative: a backup is deleted only when both its count and age limits are exceeded. Set `mode: either` to delete when either limit matches. `keep_at_least` always protects the newest successful backups. Per-target settings override global defaults.

Retention applies to the local backup directory. The sync destination keeps every file it has received unless `sync.delete` is enabled or `lutra sync --delete` is run.

`cleanup --dry-run` previews every affected path. Orphan sidecars require `--orphan-sidecars`. Untracked backup files require `--orphan-files` plus confirmation or `--force`. `--prune-history` removes old failure, verify, and sync records while preserving successful backup history.

## Restore

`lutra restore` is destructive and replaces the contents of the configured database. It asks for confirmation unless `--force` is passed. Omitting `--target` or `--file` opens interactive selection prompts.

```bash
lutra restore --target my-postgres --file /var/backups/lutra/my-postgres/latest.dump.gz
```

Custom-format dumps are restored with `pg_restore --clean --if-exists`. Plain-format dumps drop and recreate the database, then load with `psql`.

## Verify

`lutra verify` proves a backup is restorable without touching the production database. It checks the checksum, restores into a temporary `lutra_verify_<id>` database in the same container, counts the restored tables, and drops the temporary database. Results are recorded as `verify` records in history. Verification exits `0` on success and `1` on failure.

```bash
lutra verify --target my-postgres
```

Set `verify_schedule` on a database target and run `sudo lutra schedule install` to schedule restore drills.

To check a backup away from the server, restore it into a disposable container:

```bash
sha256sum -c my-postgres_<timestamp>.dump.gz.sha256
docker run -d --rm --name restore-test -e POSTGRES_PASSWORD=test postgres:17-alpine
docker exec restore-test psql -U postgres -c 'CREATE DATABASE restore_test'
gunzip -c my-postgres_<timestamp>.dump.gz \
  | docker exec -i restore-test pg_restore -U postgres -d restore_test --no-owner --exit-on-error
docker stop restore-test
```

Use a PostgreSQL image at least as new as the server that produced the dump.

## Offsite Copies

Lutra can push backups with its optional rsync configuration. A pull from a Raspberry Pi or local machine provides stronger isolation and is also supported:

```bash
rsync -avz --exclude='/.lutra-state/' vps:/var/backups/lutra/ ~/backups/lutra/

# Example local cron entry
0 6 * * * rsync -avz --exclude='/.lutra-state/' vps:/var/backups/lutra/ ~/backups/lutra/
```

For a Raspberry Pi, use a dedicated restricted user and grant its SSH key write access only to the destination directory. A pull-based timer is safer because a compromised VPS cannot use the Pi's credentials to delete the repository. Built-in sync excludes local application state, lock files, and temporary files. Apply equivalent exclusions to manual pull tools; do not copy or snapshot the live `lutra.db`, `lutra.db-wal`, or `lutra.db-shm` family.

Dumps are stored unencrypted on both sides. Restrict the destination directory to the account that receives the sync.

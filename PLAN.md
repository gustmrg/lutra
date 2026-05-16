# Lutra Implementation Plan

This plan turns the reliability review and product feedback into phased implementation work. The main direction is:

- Lutra owns database-aware backup, validation, restore verification, health, metadata, and local retention.
- External tools such as `rsync`, restic, Borg, or Kopia can still own full repository storage and transport.
- If Lutra adds Raspberry Pi/offsite support, it should start as a thin SSH/rsync workflow rather than a broad cloud-storage abstraction.

## Phase 0: Baseline and Documentation Alignment

Goal: make the documented product match the current code and remove obvious setup mismatches.

### Tasks

- Update `README.md` command list to include implemented commands:
  - `lutra health`
  - `lutra config generate`
- Document `config generate` behavior and limitations.
- Document `health` exit codes and intended use from cron/systemd/monitoring.
- Fix or verify setup templates so generated schedules use systemd calendar syntax, not cron syntax.
- Add a short "Project scope" section:
  - Lutra creates database dumps.
  - Lutra manages local retention and backup metadata.
  - Offsite transfer is currently external unless `sync` is enabled later.
- Add a "Restore status" note that restore/test-restore is not yet implemented.

### Acceptance Criteria

- README reflects all current CLI commands registered in `Program.cs`.
- New users are not shown cron syntax where systemd calendar syntax is required.
- The README clearly states what Lutra does and does not currently guarantee.

## Phase 1: Backup Safety and Concurrency

Goal: prevent backup/history corruption and partial files before adding higher-level features.

### Tasks

- Add per-target backup locking.
  - Prevent two runs of the same target from executing concurrently.
  - Make lock behavior explicit: fail fast by default, with an optional wait mode later if useful.
- Add a history-file lock around all `backup-history.json` read-modify-write operations.
- Make backup filenames collision-resistant.
  - Include sub-second precision or a short unique suffix.
  - Keep filenames human-readable.
- Write backups to a temporary host path first, then atomically move to the final path after success.
- Delete temporary/partial files on failure.
- Make SQL Server container temp paths unique per run instead of using `/tmp/lutra_backup.bak`.
- Ensure failed backups record useful error metadata without leaving valid-looking artifacts.

### Acceptance Criteria

- Two simultaneous runs for the same target cannot write the same host file.
- Concurrent history writes cannot lose records.
- Failed backups do not leave final-path files behind.
- SQL Server backup temp files cannot be clobbered by overlapping runs.
- Existing `backup run`, `history`, and `cleanup` behavior remains compatible.

## Phase 2: Integrity Metadata and Reconciliation

Goal: make each backup independently inspectable and offsite-sync friendly.

### Tasks

- Generate a SHA-256 checksum for every completed backup.
- Write a sidecar checksum file, for example:
  - `<backup-file>.sha256`
- Add checksum fields to history records.
- Generate a per-backup manifest sidecar, for example:
  - `<backup-file>.json`
- Manifest should include:
  - target name
  - database type
  - database name
  - container name
  - backup filename
  - file size
  - SHA-256 checksum
  - compression type
  - backup format
  - start/end timestamps or duration
  - Lutra version
  - success status
- Add `lutra backup verify-file` or equivalent local integrity check.
  - Verify file exists.
  - Verify checksum matches.
  - Report missing manifest/checksum clearly.
- Add a reconciliation command or mode.
  - Find files without history entries.
  - Find history entries whose files are missing.
  - Find missing checksum/manifest sidecars.
  - Start read-only; add repair options later.

### Acceptance Criteria

- Every successful backup has a checksum and manifest.
- History can show checksum metadata.
- A local integrity command can detect changed, missing, or truncated files.
- Syncing a target directory to a Raspberry Pi preserves enough metadata to validate backups there.

## Phase 3: Strong Configuration and Preflight Validation

Goal: catch broken setups before scheduled backups silently fail.

### Tasks

- Expand `lutra config validate` into layered validation:
  - YAML/schema validation.
  - Semantic validation.
  - Optional runtime preflight validation.
- Add semantic checks:
  - duplicate target names
  - positive `retention.max_count`
  - non-negative or positive `retention.max_age_days`, based on chosen semantics
  - valid database type specific options
  - valid compression value
  - valid PostgreSQL format
  - required username/password fields where appropriate
  - referenced environment variables exist
- Add schedule validation for systemd calendar expressions.
  - Prefer invoking `systemd-analyze calendar` when available.
  - Fall back to clear warnings if the host does not provide systemd tools.
- Add backup directory checks:
  - directory exists or can be created
  - writable by the running user
  - permissions are not overly broad for sensitive locations
- Add Docker preflight checks:
  - Docker CLI exists.
  - Docker daemon is reachable.
  - container exists and is running.
  - required dump tool exists in the container:
    - `pg_dump`
    - `mongodump`
    - `/opt/mssql-tools18/bin/sqlcmd` or detected equivalent
- Add an explicit `--preflight` or `--runtime` option if full Docker checks are too expensive for default validation.

### Acceptance Criteria

- `lutra config validate` catches common configuration mistakes without running a backup.
- Runtime checks can confirm Docker/container/tool availability.
- Validation messages tell the user exactly what to fix.
- README documents lightweight validation versus runtime preflight behavior.

## Phase 4: Restore and Test-Restore

Goal: prove backups are restorable, not merely creatable.

### Tasks

- Add restore provider interfaces matching the backup providers.
- Add `lutra restore` for explicit restore workflows.
  - Select target.
  - Select backup.
  - Require confirmation before destructive restores.
  - Support non-interactive flags for automation.
- Add `lutra verify` or `lutra test-restore`.
  - Restore into a disposable database/container where practical.
  - Run a minimal validation query or collection listing after restore.
  - Never overwrite production data.
- PostgreSQL:
  - Support custom format restore with `pg_restore`.
  - Support plain SQL restore with `psql`.
  - Test-restore into a temporary database or disposable container.
- MongoDB:
  - Support archive restore with `mongorestore --archive`.
  - Avoid passing passwords as process arguments where possible.
  - Test-restore into a disposable container or explicitly named temporary database.
- SQL Server:
  - Support restore from `.bak`.
  - Use unique container paths.
  - Test-restore into a temporary database name.
- Record verification results in history or a separate health record.

### Acceptance Criteria

- A user can restore a selected backup intentionally.
- A user can run a non-destructive test-restore.
- Verification failure is visible in CLI output and machine-readable exit codes.
- Restore/test-restore workflows are documented with safety warnings.

## Phase 5: Health and Notifications

Goal: make failures visible without requiring manual inspection.

### Tasks

- Stabilize and document `lutra health`.
- Add health checks for:
  - most recent successful backup age
  - consecutive failures
  - backup size anomalies
  - duration anomalies
  - missing files referenced by history
  - checksum verification failures
  - missing offsite sync marker if sync is implemented later
- Add notification configuration.
  - Start with simple webhooks.
  - Add Healthchecks.io-compatible ping URLs.
  - Consider email later if it can be done without heavy dependencies.
- Notify on:
  - backup failure
  - restore/test-restore failure
  - no recent successful backup
  - checksum mismatch
  - sync failure, if sync exists
- Add machine-readable output where useful:
  - `--json` for `health`
  - predictable exit codes

### Acceptance Criteria

- Scheduled environments can detect unhealthy backup state via exit code.
- Users can configure at least one lightweight notification channel.
- Health findings are actionable and not just informational.

## Phase 6: Raspberry Pi and Offsite Workflow Support

Goal: support the original self-hosted offsite backup use case without building a full cloud-backup platform.

### Tasks

- Decide whether sync remains external documentation or becomes a first-class command.
- If first-class, add a small `sync` config section:
  - type: `rsync`
  - host
  - user
  - destination path
  - SSH key path
  - optional port
  - optional extra rsync args
- Add `lutra sync`.
  - Sync backup directories, manifests, and checksums.
  - Support `--target`.
  - Support `--dry-run`.
  - Support `--delete` only when explicitly configured or passed.
- Add `lutra sync validate` or include sync checks in preflight:
  - SSH connectivity
  - destination path exists or can be created
  - remote write permission
  - rsync installed locally and remotely
- Add optional post-backup sync:
  - run sync after successful backup
  - record sync status in history/manifest
- Document recommended Raspberry Pi setup:
  - SSH key
  - restricted user
  - destination permissions
  - optional pull-based cron from the Pi as a safer alternative
- Keep compatibility with restic/Borg/Kopia by documenting how to point those tools at Lutra's backup directory.

### Acceptance Criteria

- The Raspberry Pi workflow is either fully documented as external or supported by `lutra sync`.
- If `lutra sync` exists, it supports dry-run and target-specific sync.
- Sync status is visible enough for health checks and troubleshooting.

## Phase 7: Cleanup, Retention, and Lifecycle Improvements

Goal: make cleanup safer and easier to reason about.

### Tasks

- Add `lutra cleanup --dry-run`.
- Show exactly which files, manifests, checksums, and history entries would be removed.
- Apply retention to sidecar files with their backup file.
- Decide whether retention should require both max count and max age, or support configurable modes:
  - delete when both conditions match
  - delete when either condition matches
  - keep at least N newest regardless of age
- Add orphan cleanup options:
  - remove sidecars without backup file
  - remove backup files not tracked in history only with explicit confirmation
- Add `history prune` or equivalent if history grows too large.

### Acceptance Criteria

- Users can preview cleanup safely.
- Retention never leaves dangling sidecar files for deleted backups.
- Retention semantics are documented and covered by tests.

## Phase 8: Automated Tests and Release Hardening

Goal: make the tool safe to evolve.

### Tasks

- Add unit tests for:
  - config parsing and validation
  - retention logic
  - filename generation
  - manifest/checksum generation
  - history locking behavior
  - health anomaly detection
- Add integration tests where practical:
  - fake `IProcessExecutor` tests for backup providers
  - Docker-based tests gated behind an opt-in flag
  - restore/test-restore flows for disposable containers
- Add CLI smoke tests:
  - `config validate`
  - `backup list`
  - `cleanup --dry-run`
  - `health`
- Add release checks:
  - build self-contained linux-x64 and linux-arm64 binaries
  - verify generated tarballs contain expected binary names
  - verify install instructions match release artifact names
- Add CI if not already present.

### Acceptance Criteria

- Core behavior is covered by repeatable automated tests.
- Release artifacts match README install commands.
- Risky backup/retention/restore behavior has regression coverage.

## Phase 9: Advanced Database Recovery Features

Goal: add deeper database-specific capabilities only after the core backup system is trustworthy.

### Tasks

- PostgreSQL:
  - WAL archiving support.
  - Point-in-time recovery documentation or automation.
  - Optional integration guidance for pgBackRest/WAL-G when Lutra is not the right tool.
- SQL Server:
  - differential backups.
  - transaction log backups.
  - restore chains.
- MongoDB:
  - oplog-aware backups for replica sets.
  - sharded cluster guidance if needed.
- Add database-version compatibility checks.
- Add restore drills as scheduled health checks if feasible.

### Acceptance Criteria

- Advanced features do not compromise the simple dump-based workflow.
- Documentation clearly explains when to use Lutra versus specialist tools.

## Suggested Implementation Order

1. Phase 0: Baseline and documentation alignment.
2. Phase 1: Backup safety and concurrency.
3. Phase 2: Integrity metadata and reconciliation.
4. Phase 3: Strong configuration and preflight validation.
5. Phase 4: Restore and test-restore.
6. Phase 5: Health and notifications.
7. Phase 6: Raspberry Pi/offsite workflow support.
8. Phase 7: Cleanup and retention improvements.
9. Phase 8: Automated tests and release hardening.
10. Phase 9: Advanced database recovery features.

If this needs to be shortened into a first milestone, implement only:

1. per-target locking
2. history locking
3. temp-file-to-atomic-move backups
4. checksum sidecars
5. stronger `config validate`
6. `cleanup --dry-run`
7. README alignment

That milestone would make Lutra much safer without expanding the product surface too much.

# Plan 001: Introduce Lutra's SQLite application database and migrate backup history

> **Executor instructions**: Execute exactly one phase at a time. At the start
> of a phase, set its status below to `In Progress`. Run every verification
> gate for that phase. When all gates pass, set the phase to `Done` and its
> review to `Pending`, then STOP and report the diff, verification results, and
> remaining risks. Do not begin the next phase until the maintainer explicitly
> approves the previous checkpoint. At the beginning of the next phase, record
> that approval in the table. Keep `plans/README.md` synchronized.
>
> **Drift check (run first)**:
> `git diff --stat b69eb68..HEAD -- src tests docs .github .gitignore Lutra.slnx`
>
> If an in-scope implementation has changed since this plan was written,
> compare the current-state evidence below with the live code. Stop and report
> any semantic mismatch instead of applying this plan mechanically.

## Status

- **Priority**: P1
- **Effort**: L (multi-day, four checkpoints)
- **Risk**: MEDIUM
- **Depends on**: none
- **Category**: migration / correctness / architecture
- **Planned at**: commit `b69eb68`, 2026-08-01

### Phase checkpoints

| Phase | Deliverable | Status | Review |
|---|---|---|---|
| 1 | Generic Lutra database foundation and release packaging | Done | Approved 2026-08-01 |
| 2 | Automatic migration and production cutover | Done | Approved 2026-08-01 |
| 3 | Persistent states, leases, and safe sync coordination | Done | Pending |
| 4 | Linux process tests, documentation, and final release verification | TODO | — |

When a phase is complete, use exactly `Done` in the Status column. A phase may
be `Done` while its Review remains `Pending`. If review requests changes, set
the phase back to `In Progress`, apply only those changes, rerun its gates, and
return it to `Done` before stopping again.

## Outcome and storage location

SQLite will be the application-state database for Lutra. Backup history is its
first domain, not the identity or lifetime boundary of the database. The
authoritative file will always be named `lutra.db` and stored under the
resolved application state directory:

```text
<state_directory>/lutra.db
```

Default locations:

```text
# System-wide installation
/var/lib/lutra/lutra.db

# User installation when XDG_STATE_HOME is set
$XDG_STATE_HOME/lutra/lutra.db

# User fallback
~/.local/state/lutra/lutra.db
```

SQLite WAL may create these transient companion files in the same state
directory:

```text
lutra.db-wal
lutra.db-shm
```

The live application database and its WAL/SHM files are local operational
state. They must never be placed in the backup tree, included in `lutra sync`,
added to disaster-recovery bundles, or exported automatically. This feature
does not create `lutra.snapshot.db` or any equivalent database snapshot.

Future application features may add their own tables and migrations to
`lutra.db` without changing its path/name or depending on the backup-history
repository. The state directory belongs to the installed application, but is
deliberately separate from the executable directory (`/usr/local/bin` or
`~/.local/bin`): binaries are replaceable and may be read-only, while
`/var/lib/lutra` or the XDG state directory has the correct persistence and
lifecycle for mutable local application data.

Losing `lutra.db` loses local history and in-flight application states, but it
must not invalidate or delete backup artifacts, manifests, or checksums. The
current reconciliation command remains a read-only way to identify artifacts
that no longer have history records; rebuilding/importing application state
from artifacts is a separate future feature, not an implicit part of sync.

## Why this matters

Lutra installs one systemd service per backup target, so pg_dump, mongodump,
verify, and sync processes can overlap. All of them currently share one JSON
history file. The original fixed-temp collision has already been mitigated by
a GUID temp name and a Linux file lock, but the implementation still rewrites
the complete history for every mutation, serializes reads behind the same
lock, has no proven multi-process regression test, and cannot represent live
states such as creating, uploading, or verifying.

SQLite provides atomic state transitions, safe concurrent access from the
separate systemd processes, indexed queries, one-time migration of existing
history, and recovery of operations whose process dies. The storage layer must
be application-oriented: `LutraDatabase` owns connections and schema
migrations, while a history repository owns only backup-domain queries.
Future domains must use separate repositories/tables rather than enlarging the
history abstraction or turning `app_metadata` into a generic key/value store.
Keeping this DB local also prevents future unrelated or sensitive application
state from silently becoming offsite backup content merely because history was
the first domain implemented.

## Current state

### Confirmed concurrency shape

- `src/Lutra.CLI/Commands/Schedule/ScheduleInstallCommand.cs:39-48` loops over
  targets and installs `lutra-backup-<target>.service`; verify receives another
  independent unit.
- `src/Lutra.CLI/Commands/Schedule/ScheduleInstallCommand.cs:128-153` writes a
  oneshot service whose command is `backup run --target <name>` plus a target
  timer. Separate target services therefore run as separate processes.

### Current JSON behavior

- `src/Lutra.Core/History/BackupHistoryService.cs:17-20` fixes the paths to
  `backup-history.json` and `.backup-history.lock` at the backup root.
- `src/Lutra.Core/History/BackupHistoryService.cs:23-31` performs a locked
  read-modify-write for each appended record.
- `src/Lutra.Core/History/BackupHistoryService.cs:84-104` deserializes the
  whole file, serializes the whole list to a GUID temp path, and replaces the
  history file.
- `src/Lutra.Core/History/BackupHistoryService.cs:107-146` combines a static
  in-process semaphore with a retrying file lock. This substantially mitigates
  lost updates on Linux.
- `src/Lutra.Core/History/BackupHistoryService.cs:150-154` deliberately skips
  the file lock on macOS, so local macOS tests cannot validate the Linux
  systemd scenario.
- `src/Lutra.Core/History/IBackupHistoryService.cs:7-10` still documents that
  concurrent writers are not required, contradicting current deployment and
  test intent.
- `tests/Lutra.Core.Tests/HistoryAndRetentionTests.cs:10-27` launches 20 tasks
  against one service instance. The static semaphore is exercised, but two OS
  processes are not.

### Current storage configuration

- `src/Lutra.Core/Configuration/BackupConfig.cs:19-27` exposes only
  `BackupDirectory` and explicitly says that both artifacts and history live
  there; there is no application-state boundary today.
- `src/Lutra.Core/Configuration/ConfigTemplates.cs:11-41` derives only config
  and backup directories and emits only `backup_directory` in generated YAML.
- `setup.sh:191-201,309-321` likewise creates only executable, config, and
  backup directories. Introducing `/var/lib/lutra` or an XDG state directory
  therefore requires coordinated setup/config/uninstall changes, not merely a
  different constant inside the history repository.
- `src/Lutra.CLI/Commands/Uninstall/UninstallCommand.cs:23-46,90-149` discovers
  and optionally deletes the backup directory but has no independent state
  lifecycle. Moving the database out of the backup tree without updating this
  command would leave state behind unexpectedly or make history preservation
  ambiguous.

### Current terminal-only records

- `src/Lutra.Core/Backup/BackupOrchestrator.cs:242-307` creates no history row
  until the artifact, checksum, and manifest are complete.
- `src/Lutra.Core/Backup/BackupOrchestrator.cs:330-352` appends a separate
  failure row only after an exception.
- `src/Lutra.Core/Restore/RestoreOrchestrator.cs:527-550` records verify only at
  completion and swallows history failures.
- `src/Lutra.Core/Sync/RsyncService.cs:79-121` executes rsync first and writes
  terminal sync records afterward.
- `src/Lutra.CLI/Commands/History/HistoryCommand.cs:35-60` can display only
  boolean OK/FAILED states.

### Additional hazards this plan must address

- `src/Lutra.Core/Sync/RsyncService.cs:68-99` syncs the target or complete
  backup tree without taking `TargetLock`; a manual sync can overlap an active
  artifact write and copy temporary or incomplete artifact/sidecar sets.
- `src/Lutra.Core/Backup/BackupOrchestrator.cs:335-352` deletes a finalized
  artifact when the success-history write fails, then attempts another write
  through the same failing store. A valid backup should be retained and
  reconciled instead.
- `src/Lutra.CLI/Commands/Schedule/ScheduleInstallCommand.cs:130-137` does not
  set `User=`. Scheduled commands normally run as root while manual commands
  may use another account, which can create ownership conflicts for the main
  DB, WAL, and SHM files.
- `src/Lutra.CLI/Lutra.CLI.csproj` and `.github/workflows/ci.yml:58-75` require
  self-contained, single-file Linux releases. The native SQLite library must
  be embedded instead of becoming an extra archive file.

## Decisions locked by this plan

### Provider and durability

- Add exact package references `Microsoft.Data.Sqlite` `10.0.10` and
  `SQLitePCLRaw.bundle_e_sqlite3` `2.1.12`. The direct bundle pin overrides the
  vulnerable native `2.1.11` selected by the provider's default transitive
  dependency. Do not upgrade to the incompatible 3.x line during this plan.
- Use direct ADO.NET commands, not EF Core or another ORM.
- Resolve an absolute state-directory path first, then use one short-lived
  connection per database/repository method.
- Configure `journal_mode=WAL`, `synchronous=FULL`, foreign keys on, pooling on,
  default/private cache, and a 30-second busy timeout. Do not use shared cache
  with WAL.
- Microsoft.Data.Sqlite async methods execute synchronously. Keep the existing
  Task-shaped public interface to limit caller churn, but execute SQLite calls
  synchronously inside it and check cancellation before connection/command
  boundaries. Do not wrap each command in `Task.Run`.
- WAL requires a local filesystem with all writers on the same host. NFS or a
  multi-host shared `state_directory` is unsupported.

### State-directory contract and compatibility

- Add top-level YAML property `state_directory`. New setup/config templates
  always write an explicit absolute path.
- Resolution for legacy configurations that omit it:
  1. A config resolved under `/etc/lutra` uses `/var/lib/lutra`.
  2. Every other config uses `<backup_directory>/.lutra-state` as a
     compatibility fallback so root/manual invocations of the same config do
     not silently select different databases. `config validate` warns and
     recommends making `state_directory` explicit. Do not derive an omitted
     value from the current process HOME/XDG environment.
- New per-user setup/config templates explicitly write
  `$XDG_STATE_HOME/lutra` when `XDG_STATE_HOME` is absolute, otherwise the
  user's absolute `~/.local/state/lutra` path; they therefore do not use the
  legacy compatibility fallback.
- One resolved state directory represents one logical Lutra installation and
  contains exactly one authoritative `lutra.db`. Independent custom configs
  must use distinct state directories in v1; intentionally sharing one DB
  across unrelated configs is unsupported because target names and future
  domain identifiers do not yet have a configuration namespace. Document this
  constraint and make `config validate` warn when an explicit state directory
  is already owned by a different normalized config path.
- `YamlConfigLoader` must return a resolved absolute `StateDirectory` on
  `BackupConfig`; consumers must not independently infer installation mode.
- The JSON importer always looks for legacy
  `<backup_directory>/backup-history.json`, even though the new DB is under
  `state_directory`.
- `setup.sh`, config init/generate/reset templates, installation docs, and
  uninstall discovery must understand the state directory. System installs
  create `/var/lib/lutra`; user installs create the resolved XDG/fallback path.
- Treat the state directory as persistent application data. Interactive
  uninstall displays/prompts for it separately. Add `--keep-state`; existing
  `--keep-backups` also implies preserving state so an upgrade/uninstall cannot
  discard backup history while retaining artifacts. With `--yes`, state is
  deleted only when neither preservation flag is present.

### Application database architecture

- Add `src/Lutra.Core/Persistence/LutraDatabase.cs` (and a narrow injectable
  contract only if tests need it), not a database owner under `History`. Put
  ordered migrations under `src/Lutra.Core/Persistence/Migrations/`. This
  layer owns the resolved `lutra.db` path, connection creation, PRAGMAs,
  application migrations, and integrity checks.
- Add `SqliteBackupHistoryRepository` under `History`. It depends on
  `LutraDatabase` and owns only `backup_operations` commands/mapping.
  `IBackupHistoryService` remains domain-specific; future features must not
  depend on it to access SQLite.
- Future domains add their own repository and tables through the shared
  migration runner. They must not add unrelated columns to `backup_operations`
  or store domain state in `app_metadata`.
- `app_metadata` is reserved for database/import infrastructure markers. It is
  not a public/general key-value API.
- Do not add a snapshot/export method to `LutraDatabase` in this feature.
  Exporting application state, if ever needed, requires a separate design for
  explicit user intent, content scope, encryption, retention, and restore.

### Schema version 1

Create a global `schema_migrations` table with integer `version` primary key,
unique migration name, and UTC application timestamp. Apply pending migrations
in numeric order under `BEGIN IMMEDIATE`, recording a migration in the same
transaction as its DDL. Migration `001_application_database` creates
`app_metadata`, including the owning normalized config-path marker used to
detect accidental cross-config reuse. Migration `002_backup_operations` creates
`backup_operations` with parameterized access and these logical columns:

| Column | SQLite type | Rule |
|---|---|---|
| `id` | TEXT | Primary key, GUID `N` format |
| `target_name` | TEXT | Required |
| `operation_type` | TEXT | `backup`, `verify`, or `sync` |
| `status` | TEXT | One of the seven states below |
| `started_at_unix_ms` | INTEGER | Required UTC instant |
| `updated_at_unix_ms` | INTEGER | Required UTC instant |
| `completed_at_unix_ms` | INTEGER NULL | Terminal operations only |
| `lease_id` | TEXT NULL | Required only for active operations |
| `lease_expires_at_unix_ms` | INTEGER NULL | Required only for active operations |
| `file_name` | TEXT NULL | Artifact or verified file |
| `file_size_bytes` | INTEGER NULL | Non-negative when present |
| `sha256` | TEXT NULL | Successful backups when available |
| `manifest_file_name` | TEXT NULL | Successful backups when available |
| `duration_ms` | INTEGER NULL | Preserve migrated durations |
| `error_message` | TEXT NULL | Failed/cancelled/interrupted detail |

Create indexes on `(target_name, started_at_unix_ms DESC)` and
`(status, lease_expires_at_unix_ms)`. Reads with identical timestamps use
`id DESC` as a deterministic tiebreaker. Do not add uniqueness on
target/file because legacy JSON can contain duplicates.

### Public history model and lifecycle

- Replace the overloaded string/boolean model with `HistoryOperationType`,
  `HistoryOperationStatus`, and `HistoryRecord`.
- Types: `Backup`, `Verify`, `Sync`.
- Active statuses: `Creating`, `Verifying`, `Uploading`.
- Terminal statuses: `Succeeded`, `Failed`, `Cancelled`, `Interrupted`.
- Extend `IBackupHistoryService` with `BeginOperationAsync`,
  `CompleteOperationAsync`, `FailOperationAsync`, `CancelOperationAsync`, and
  internal lease renewal. Queries return `HistoryRecord`; retention removes a
  successful backup by record ID, not target/file tuple.
- A history operation lease heartbeats every 30 seconds and expires after five
  minutes. Queries and new operation starts atomically convert expired active
  rows to `Interrupted`. Dispose without a terminal transition performs a
  best-effort interrupted transition using a fresh token capped at five
  seconds.
- A backup starts as `Creating`; verify starts as `Verifying`; sync starts as
  `Uploading`. Post-backup sync remains a separate operation rather than a
  phase of the backup row.

### Automatic legacy migration

- Database migrations and import use `BEGIN IMMEDIATE` so concurrent processes
  serialize schema creation/import.
- If `app_metadata` has no `legacy.backup_history_json.imported` marker and
  `<backup_directory>/backup-history.json` exists, deserialize and insert every
  record in the same transaction as that marker.
- Preserve the JSON byte-for-byte at its original name. New releases never
  write it again. It is audit/rollback input only; downgrade after cutover is
  unsupported because an old binary would create a divergent JSON history.
- Mapping:
  - `record_type == null` → `Backup`;
  - `verify` → `Verify`;
  - `sync` → `Sync`;
  - `success == true` → `Succeeded`; otherwise `Failed`.
- A legacy backup timestamp is its start; derive completion by adding duration.
  A verify timestamp was written at completion; derive start by subtracting
  duration. A sync timestamp is its known start and has no reliable historical
  completion instant, so use the same instant for completion while preserving
  its recorded duration.
- Empty legacy file names become SQL NULL. Preserve all other optional values.
- Invalid JSON, unknown `record_type`, a nonempty operation table without an
  import marker, or a row violating schema constraints aborts and rolls back
  the entire import with an actionable path/index error. Never partially
  import or silently skip records.

### Local-state boundary and consistent artifact sync

- `lutra sync` continues to synchronize backup-tree content; it must never add
  the resolved state directory, `lutra.db`, WAL/SHM, or a database export as an
  rsync source. Disaster-recovery bundles likewise must not contain them.
- After a successful import, the preserved `backup-history.json` is immutable
  legacy input, not an authoritative export. Exclude it together with
  `.backup-history.lock`, `.locks/`, and `*.tmp` from full-root rsync. A
  target-specific sync already starts below the backup root and cannot include
  those root-level files. Do not delete any pre-existing remote JSON copy as
  part of migration; document that such copies are stale and non-authoritative.
- Do not call `SqliteConnection.BackupDatabase`, create `lutra.snapshot.db`, or
  copy the live DB/WAL/SHM family anywhere in this feature. If an explicit
  application-state backup is requested later, design it as a separate opt-in
  export/restore capability rather than ordinary sync content.
- A target-specific sync acquires that target's existing operation lock. A
  full sync attempts all target locks in stable name order and releases all
  acquired locks if any target is busy. Do not run a partial root sync.
- On a busy target, create/finish the requested sync history rows as `Failed`
  with a retry-later message, do not invoke rsync, and return a failed result.

## Commands and expected results

| Purpose | Command | Expected result |
|---|---|---|
| Full tests | `dotnet test Lutra.slnx --configuration Release` | exit 0, all tests pass |
| x64 publish | `dotnet publish src/Lutra.CLI/Lutra.CLI.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /tmp/lutra-publish-x64` | exit 0 |
| arm64 publish | `dotnet publish src/Lutra.CLI/Lutra.CLI.csproj -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o /tmp/lutra-publish-arm64` | exit 0 |
| Package audit | `dotnet list src/Lutra.Core/Lutra.Core.csproj package --include-transitive --vulnerable` | no vulnerable packages |
| JSON write removal | `rg -n 'SaveRecordsAsync|backup-history\\.json.*Write|File\\.Move\\(.*backup-history' src` | no production JSON writer after Phase 2 |
| State export prohibition | `rg -n 'BackupDatabase|lutra\\.snapshot\\.db' src` | no matches after Phase 2 |

The verified baseline at the planned commit is 10 passing tests. A lower count
after implementation is a failure even if the command exits 0.

## Scope

### In scope

- Generic application database connection, migrations, and integrity
  infrastructure plus history contracts/repository/importer in `Lutra.Core`.
- State-directory configuration/resolution, generated templates, setup, and
  uninstall lifecycle, including `state_directory` and `--keep-state`.
- SQLite dependency/package configuration in
  `src/Lutra.Core/Lutra.Core.csproj` and native single-file embedding in
  `src/Lutra.CLI/Lutra.CLI.csproj`.
- Lifecycle integration in backup, restore verification, and rsync services,
  including `TargetLock` reuse and artifact-preservation behavior.
- Direct history consumers: CLI history/cleanup/config validation, retention,
  health, reconciliation, bundles, and restore file selection.
- Unit/integration tests under `tests/`, CLI smoke/release checks in
  `.github/workflows/ci.yml`, `.gitignore`, and affected user/developer docs.
- This plan and `plans/README.md`, which must be updated at every checkpoint.

### Out of scope

- Changing database dump/restore commands or backup file formats.
- Adding user-configurable lease, PRAGMA, or SQLite tuning knobs beyond the
  application-level `state_directory` path.
- Automatically changing systemd `User=`/`Group=`. Add validation and docs for
  consistent execution identity, but handle service identity in a separate
  follow-up because changing it can break Docker or protected file targets.
- Supporting NFS, multiple hosts writing one database, downgrades that resume
  JSON writes, or importing arbitrary externally-created SQLite schemas.
- Encrypting `lutra.db`. It inherits state-directory access controls; secrets
  must not be deliberately added to history fields or `app_metadata`.
- Exporting, snapshotting, syncing, bundling, or remotely restoring application
  state. Those require a separate opt-in feature and security review.

## Git workflow

- Use a `codex/`-prefixed branch if a branch is requested.
- Follow the repository's Conventional Commit style, for example
  `feat: add advanced database recovery workflows` and
  `fix(config): resolve installation paths reliably`.
- Keep one logical commit per reviewed phase if commits are requested.
- Do not push or open a pull request unless explicitly instructed.

## Implementation phases

### Phase 1: Add the generic Lutra database foundation without switching history

1. Set Phase 1 to `In Progress` and the Plan 001 index row to `IN PROGRESS`.
2. Add the two exact package references. Add
   `IncludeNativeLibrariesForSelfExtract=true` to the CLI publish properties;
   do not relax the CI requirement that release archives contain one binary.
3. Add `state_directory` parsing/resolution and update new-install config
   templates plus `setup.sh` to create the explicit state directory. Existing
   configurations continue loading through the three-case fallback contract.
4. Add `LutraDatabase`, ordered `schema_migrations`, `app_metadata`, integrity,
   and config-ownership validation. Add the first domain migration, typed
   history models, and `SqliteBackupHistoryRepository`, but keep
   `BackupHistoryService` and `ServiceFactory` unchanged so production still
   writes JSON during this checkpoint.
5. Implement parameterized terminal CRUD, filtering, deterministic ordering,
   remove-by-ID, and prune rules in the history repository.
6. Add path-resolution, migration-runner, and focused repository tests using
   separate service instances and temporary directories. Execute at least 100
   concurrent inserts and assert exact IDs, counts, target filtering, migration
   ledger contents, and `PRAGMA integrity_check = ok`.
7. Publish both Linux RIDs and assert the output/archive has exactly one
   executable and no standalone `libe_sqlite3.so`. Run the package audit and
   confirm `2.1.12` resolved.
8. Run the full suite. Set Phase 1 to `Done`, Review to `Pending`, update this
   document with the verification summary, and STOP.

**Phase 1 gate**: all tests pass; x64 and arm64 remain single-file; package
audit is clean; `ServiceFactory` still selects the JSON implementation.

**Phase 1 verification (2026-08-01)**:

- `dotnet test Lutra.slnx --configuration Release`: 17 passed, 0 failed.
- Linux x64 and arm64 self-contained publishes: one executable per output and
  one archive member per release archive; no standalone `libe_sqlite3.so`.
- Package audit: no vulnerable packages; `Microsoft.Data.Sqlite` resolved to
  `10.0.10` and every `SQLitePCLRaw` package resolved to `2.1.12`.
- Repository concurrency test: 100 exact IDs persisted through eight separate
  repository/database instances; target filtering and integrity check passed.
- `ServiceFactory` remains on `BackupHistoryService` (JSON), as required for
  this checkpoint. `bash -n setup.sh` and `git diff --check` both passed.

### Phase 2: Import JSON transactionally and switch production to SQLite

1. Record Phase 1 review approval, set Phase 2 to `In Progress`, and implement
   the automatic JSON-to-`backup_operations` migration exactly as specified.
2. Add migration tests for no JSON, all three record types, nullable fields,
   duplicates, repeated initialization, simultaneous initialization, invalid
   JSON, unknown type, and rollback. Compare pre/post JSON bytes.
3. Compose one `LutraDatabase` from the resolved `StateDirectory` and
   normalized config path, then change `ServiceFactory.CreateHistoryService`
   to return the domain interface backed by `SqliteBackupHistoryRepository`.
   Update every terminal reader/writer and retention deletion to typed
   records/IDs while preserving CLI behavior.
4. Enforce the local-state boundary in rsync and bundle composition. Full-root
   rsync excludes the preserved JSON history, old lock file, lock directory,
   and temporary files. Tests must inspect process arguments and bundle entries
   and prove they contain no resolved state-directory path, `lutra.db`,
   WAL/SHM, database snapshot/export, or legacy JSON history.
5. Remove production JSON mutation and file-lock code, retaining only the
   narrowly scoped legacy parser/importer. Do not delete the JSON or lock file.
6. Run focused migration/local-state-boundary tests, the full suite, both
   publishes, and the package audit. Set Phase 2 to `Done`, Review to `Pending`,
   record the verification summary, and STOP.

**Phase 2 gate**: existing JSON is imported once and preserved; all production
history calls use the history repository over `<state_directory>/lutra.db`; no
application DB or export is passed to rsync/bundles; terminal command behavior
remains compatible.

**Phase 2 verification (2026-08-01)**:

- `dotnet test Lutra.slnx --configuration Release`: 26 passed, 0 failed.
- Migration coverage includes absent JSON, all legacy operation types, nullable
  fields, duplicates, repeat/concurrent initialization, invalid JSON, unknown
  types, nonempty unmarked databases, rollback, and byte-identical preservation.
- CLI smoke: `lutra history` imported a legacy fixture into `lutra.db`, rendered
  the expected terminal record, and left the source JSON SHA-256 unchanged.
- Full-root and target-specific rsync arguments plus bundle entries were tested
  to exclude application state, the SQLite file family, legacy history/locks,
  temporary files, and any generated snapshot/export.
- Linux x64 and arm64 self-contained publishes each contain one executable and
  no standalone `libe_sqlite3.so`; package audit is clean and all
  `SQLitePCLRaw` packages resolve to `2.1.12`.
- Production `ServiceFactory` selects `SqliteBackupHistoryRepository`; JSON
  mutation code was removed. `bash -n setup.sh` and `git diff --check` passed.

### Phase 3: Add persistent live states, leases, and sync coordination

1. Record Phase 2 review approval and set Phase 3 to `In Progress`.
2. Implement begin/heartbeat/terminal transitions with lease ownership checks.
   A stale or wrong lease must not overwrite a newer owner's state.
3. Integrate `Creating` around every backup target path, `Verifying` around
   test restore, and `Uploading` around rsync. Use a fresh five-second token for
   terminal cleanup after caller cancellation.
4. Update `lutra history` to render active statuses as `CREATING`, `VERIFYING`,
   or `UPLOADING`, and terminal statuses as `OK`, `FAILED`, `CANCELLED`, or
   `INTERRUPTED`. Calculate active duration from the current UTC time.
5. Update health, retention, cleanup, reconcile, bundle, and restore selection:
   active rows are excluded from terminal analysis; interrupted/cancelled rows
   count as failed attempts; active leases are never pruned; successful backup
   selection remains restricted to completed backup records.
6. Add target-lock coordination to sync and the defensive exclusions. A busy
   target fails before process execution and records the failed attempt.
7. Change backup failure handling so a finalized artifact plus sidecars survive
   a history-completion failure and can be found by reconcile. Temporary and
   incomplete artifacts are still deleted.
8. Test every valid transition, rejection of invalid/foreign-lease transitions,
   heartbeat renewal, fake-time expiry to Interrupted, cancellation, active-row
   query/display, health/retention behavior, busy sync without process launch,
   and artifact preservation.
9. Run all gates, set Phase 3 to `Done`, Review to `Pending`, record results,
   and STOP.

**Phase 3 gate**: all three live states are observable concurrently; killed or
abandoned operations recover to Interrupted; terminal consumers cannot mistake
active rows for successes/failures; sync never reads an actively written target.

**Phase 3 verification (2026-08-01)**:

- `dotnet test Lutra.slnx --configuration Release`: 38 passed, 0 failed.
- Lifecycle tests cover all active/terminal states, valid transitions,
  terminal and foreign/stale-lease rejection, heartbeat renewal, fake-time
  expiry, best-effort scope interruption, cancellation, and active-row pruning.
- Integration tests observe `Creating` during a blocked backup and `Uploading`
  during blocked rsync, verify `Verifying` failure transitions, and prove a
  finalized artifact plus sidecars survives history completion failure and is
  visible to reconciliation.
- Busy full sync acquires target locks in stable order, releases earlier locks,
  launches no rsync process, and records all requested rows as failed with a
  retry-later message. Target and full-root state exclusions remain covered.
- Health analysis excludes active operations while treating cancelled and
  interrupted terminal attempts as failures. Retention and cleanup preserve
  active leases and successful-backup selection remains terminal-only.
- CLI smoke rendered simultaneous `CREATING`, `VERIFYING`, and `UPLOADING`
  rows with live duration. Linux x64/arm64 publishes each contain one
  executable and no `libe_sqlite3.so`; package audit is clean at `2.1.12`.
- JSON mutation and database snapshot/export searches returned no matches;
  `bash -n setup.sh` and `git diff --check` passed.

### Phase 4: Prove Linux process concurrency and finish the release contract

1. Record Phase 3 review approval and set Phase 4 to `In Progress`.
2. Add an integration test that launches separate CLI OS processes against two
   file targets sharing one backup directory. Run overlapping backups in a
   loop and assert exact successful record count, unique IDs, no SQLite busy
   failure, no corruption, and terminal state for every process. Ensure CI runs
   this on Ubuntu; do not claim macOS file locking validates old behavior.
3. Extend the CLI smoke workflow with a legacy JSON fixture, concurrent command
   invocation, automatic import, and history output/state assertions.
4. Make `config validate` report the resolved state directory, warn when a
   custom config uses the compatibility fallback, open/create `lutra.db` in
   WAL, and perform a rolled-back write probe. Return an actionable error when
   the current OS account cannot create/open the DB, WAL, or SHM. Do not change
   systemd identity. Warn and refuse normal operation when the DB ownership
   marker names a different normalized config path; explain how to select a
   separate `state_directory` instead of silently mixing application state.
5. Update uninstall to discover/display the resolved state directory, support
   `--keep-state`, preserve state when `--keep-backups` is used, and prompt for
   state deletion separately in interactive mode. Add focused command tests.
6. Update README-adjacent documentation:
   - `docs/development.md`: application DB architecture, repositories,
     migrations, and how tests/releases verify SQLite.
   - `docs/operations.md`: local state vs backup layout, states, migration,
     consequences of losing local state, preserved legacy JSON, and unsupported
     downgrade/NFS behavior.
   - `docs/commands.md`: history status meanings and Interrupted semantics.
   - `docs/configuration.md` and installation docs: `state_directory` defaults,
     custom-config fallback, setup ownership, and uninstall preservation.
   - `docs/security.md`: local filesystem, same execution account, DB/WAL/SHM
     permissions, and the prohibition on implicit state export/sync.
7. Update `.gitignore` for the exact `lutra.db`, WAL, and SHM names without
   adding a broad `*.db`/`*.sqlite` rule that could hide intentional fixtures.
8. Run the full suite, Linux process test, smoke workflow equivalent, package
   audit, and both single-file publishes. Check `git diff --check`.
9. Set Phase 4 to `Done`, Review to `Pending`, set Plan 001 in
   `plans/README.md` to `DONE`, record final verification results, and STOP for
   final review.

**Phase 4 gate**: the original two-systemd-process scenario is reproduced on
Linux and passes; legacy users upgrade without data loss; docs match runtime;
both release archives still contain only `lutra`.

## Test plan

Use `tests/Lutra.Core.Tests/HistoryAndRetentionTests.cs` as the style baseline,
but split the expanded coverage into focused history migration, lifecycle, and
integration test files when one file becomes difficult to scan.

Required scenarios:

- `state_directory` resolves correctly for explicit, system default, user XDG,
  user fallback, and custom-config compatibility cases.
- Independent configs cannot accidentally reuse one state directory; the same
  config remains accepted through equivalent normalized paths.
- Generic application migrations are ordered, transactional, recorded once,
  and idempotent across database/repository instances.
- Concurrent inserts preserve every ID and leave integrity OK.
- Legacy backup, verify, sync, success, failure, optional metadata, and
  duplicates import exactly once.
- Invalid JSON/unknown types/nonempty-unmarked DB roll back without modifying
  JSON or leaving partial rows.
- Query ordering/filtering, remove by ID, and prune match existing behavior.
- Full and target-specific sync arguments plus bundle entries never contain the
  state directory, SQLite file family, a generated DB export, or legacy JSON.
- All active/terminal transitions work; invalid transitions and wrong leases
  fail without mutation.
- Heartbeat prevents false interruption; fake-time expiry marks abandoned rows.
- Cancellation records Cancelled; process death is recovered as Interrupted.
- Active operations do not affect latest-success, anomaly, retention, bundle,
  reconcile, or restore selection.
- Busy target prevents rsync launch; legacy-history, temporary-file, and
  live-state exclusions are present.
- Finalized artifacts survive history finalization failure.
- Separate Linux processes can write concurrent target histories without loss,
  corruption, or unresolved busy errors.
- Setup/config templates expose the right state path; validation catches DB,
  WAL, and SHM permission failures; uninstall preserves/deletes state according
  to interactive choice, `--keep-state`, `--keep-backups`, and `--yes`.
- x64 and arm64 release outputs contain one executable and no native sidecar.

## Done criteria

- [ ] All four phase rows are `Done` and reviews are recorded.
- [ ] `plans/README.md` marks Plan 001 `DONE`.
- [ ] `dotnet test Lutra.slnx --configuration Release` exits 0 with all old and
      new tests passing.
- [ ] Linux multi-process integration reproduces simultaneous target services
      and retains every record.
- [ ] Existing JSON migrates transactionally once and remains byte-identical.
- [ ] No production code writes `backup-history.json`.
- [ ] `<state_directory>/lutra.db` is the sole authoritative live application
      store; history is accessed only through its domain repository.
- [ ] Future repositories can add independent ordered migrations/tables without
      changing the database name/path or history contracts.
- [ ] Sync and bundles never read or export the local state DB/WAL/SHM family;
      no `lutra.snapshot.db` or equivalent automatic snapshot exists.
- [ ] `rg -n 'BackupDatabase|lutra\.snapshot\.db' src` returns no matches.
- [ ] New system/user installs write explicit state paths; existing default and
      custom configurations resolve deterministically.
- [ ] Uninstall treats state separately and honors both preservation flags.
- [ ] Creating, verifying, uploading, succeeded, failed, cancelled, and
      interrupted states are persisted and displayed correctly.
- [ ] Package audit reports no vulnerability and resolves native bundle 2.1.12.
- [ ] Both Linux RIDs publish as one executable.
- [ ] `git diff --check` exits 0 and no out-of-scope behavior changed.

## STOP conditions

Stop and report instead of improvising if any of these occurs:

- Current history/schedule/sync semantics differ materially from the evidence
  recorded above because the repository drifted after `b69eb68`.
- `Microsoft.Data.Sqlite` 10.0.10 cannot resolve with the explicitly pinned safe
  2.1.12 bundle, or the audit reports a vulnerability in the resolved graph.
- Native embedding cannot keep both Linux releases as one executable.
- WAL cannot be enabled or the resolved state directory is discovered to be NFS
  or a multi-host shared filesystem.
- Legacy JSON contains an operation type or data shape not covered here; do not
  silently coerce or drop it.
- Correct migration requires deleting, renaming, or rewriting the legacy JSON.
- Any requirement emerges to export, snapshot, sync, bundle, or remotely
  restore `lutra.db` as part of this feature; report it as a separate product
  and security decision instead of expanding scope.
- Implementing sync coordination requires changing dump/restore formats or
  automatically changing systemd execution identity.
- Any phase verification fails twice after a reasonable scoped correction.
- Work for a later phase appears necessary to make the current checkpoint
  compile or remain safe; report the dependency and revise this plan first.

## Maintenance notes

- Review all SQL for parameter binding; schema/PRAGMA strings may be constants,
  but target names, paths, errors, IDs, timestamps, and metadata must never be
  interpolated into SQL.
- Keep schema evolution in the global `schema_migrations` ledger. Every future
  feature owns a named, ordered, transactional migration plus an upgrade test;
  do not use `PRAGMA user_version` as a competing migration source of truth.
- Treat `lutra.db` as local operational state, not as a backup artifact. A
  future export feature must be explicit, opt-in, encrypted when appropriate,
  restorable, and reviewed against every domain then stored in the database.
- Lease timings are intentionally fixed for v1. Make them configurable only if
  operational evidence shows false interruptions.
- A separate follow-up should decide and document the systemd service account.
  This plan only validates that whichever account runs a command can use the
  complete SQLite file family.
- The focused audit covered history, scheduling, backup/verify/sync integration,
  direct history consumers, and release packaging. It was not a repository-wide
  correctness or security audit.

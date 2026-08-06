# Plan 002: Add structured, reconstructible environment backups

> **Linear issue**: [SWE-87](https://linear.app/gustavomrg/issue/SWE-87/criar-backup-estruturado-e-reconstrutivel-da-configuracao-do-ambiente)
>
> **Executor instructions**: Execute exactly one phase at a time. At the start
> of a phase, set its status below to `In Progress` and keep `plans/README.md`
> synchronized. Run every verification gate for that phase. When all gates
> pass, set the phase to `Done` and its review to `Pending`, then STOP and report
> the diff, verification results, and remaining risks. Do not begin the next
> phase until the maintainer explicitly approves the previous checkpoint.
>
> **Drift check (run first)**:
> `git diff --stat 6ad2eb3..HEAD -- src tests docs .github setup.sh lutra.example.yaml plans`
>
> If relevant implementation changed after this plan was written, compare the
> current-state evidence below with the live code. Stop and report semantic
> mismatches instead of applying the plan mechanically.

## Status

- **Linear**: SWE-87, `In Progress`
- **Priority**: P1 (High)
- **Effort**: L (multi-day, four review checkpoints)
- **Risk**: MEDIUM
- **Depends on**: Plan 001 Phase 3 for Phase 1; Plan 001 completed before Phase 2
- **Category**: disaster recovery / security / operations
- **Planned at**: commit `6ad2eb3`, 2026-08-05

### Phase checkpoints

| Phase | Deliverable | Status | Review |
|---|---|---|---|
| 1 | Recovery-set contract, configuration, and safe archive primitives | Done | Approved 2026-08-05 |
| 2 | Backup orchestration, inventory, retention, scheduling, and logs | TODO | - |
| 3 | Guarded, idempotent restore and post-restore validation | TODO | - |
| 4 | Clean-environment reconstruction drill, documentation, and release verification | TODO | - |

## Outcome

Lutra will produce a self-describing, versioned environment recovery set that
contains the operational material needed to reconstruct a VPS from a clean OS:

- original application and service configuration files selected through file
  targets, including NGINX, systemd units, Compose files, and application files;
- selected non-database persistent data from file or Docker volume targets;
- a machine-readable and human-readable host inventory covering the OS,
  packages, tool versions, containers, images, networks, volumes, enabled and
  running services, and firewall state;
- explicit secret exclusions and a generated checklist of values that must be
  restored from an external secret service or supplied manually;
- a manifest describing every member, checksum, source category, ownership,
  mode, dependencies, and restore order;
- sanitized backup/restore logs and an explicit result for every step.

An administrator will be able to install the documented prerequisites on a
clean Linux host, inspect and verify a recovery set, run a dry-run, apply
it, and rerun it safely. The restore must converge on the same file and volume
content without appending duplicate configuration or silently starting services.

This is a focused recovery mechanism, not general Infrastructure as Code. It
does not provision a VPS, create cloud resources, recreate users or SSH access,
install every inventoried package automatically, restore databases, export
Lutra's live `lutra.db` application state, or back up secret values.

This first implementation is intentionally plaintext for a small side-project
VPS. It does not satisfy SWE-87's original encrypted-secret criterion. Secret
values must live in an external service such as Azure Key Vault or be restored
manually. Encrypted secret inclusion is a separate follow-up after this recovery
workflow is proven useful.

## Current state

- `FileArchive` already creates tar archives with root-relative entry names and
  preserves modification time and Unix mode, but has no versioned aggregate
  manifest and uses direct extraction for restore.
- `DockerVolumeArchive` can archive and destructively restore named volumes
  through an Alpine helper container.
- `InventoryService` writes a best-effort Markdown snapshot for Docker,
  packages, systemd, crontabs, and UFW. It intentionally omits secret values and
  cron commands, and collector failures currently do not fail capture.
- `AgeEncryption` remains available for ordinary targets and bundles, but is
  deliberately outside this first environment-recovery implementation.
- `DisasterRecoveryBundleService` copies the latest artifact for every target,
  a YAML reference, environment-variable names, inventory, and generated
  instructions into a tarball. Its own documentation explicitly says it does
  not reconstruct packages, users, firewall rules, or full system state.
- `BackupOrchestrator` already provides checksummed/manifisted backup artifacts,
  retention, history lifecycle, and file/volume implementations that should be
  reused rather than reimplemented independently.
- Full-root `lutra sync` already excludes local application state and includes
  ordinary backup-tree content. Environment recovery sets stored under the
  backup root can use that path without ever copying `lutra.db`, WAL, or SHM.

## Decisions locked by this plan

### Product and command boundary

- Add a distinct `environment` command branch with `backup`, `inspect`, and
  `restore` subcommands. Do not overload the existing `bundle` command: bundles
  select latest database and target artifacts, while an environment recovery
  set is one coherent, versioned reconstruction artifact.
- Add an optional top-level `environment` configuration. It references existing
  file and volume target names instead of introducing a second file/volume
  backup model. Database targets are rejected in this list and remain restored
  through the existing database workflow.
- The configuration also declares systemd units and Docker containers that may
  be stopped/started during restore. The artifact may contain typed action data,
  but never arbitrary shell commands to execute.
- Reserve the internal history target name `@environment`; reject a configured
  target with that exact name. Record environment backup attempts through the
  existing backup operation lifecycle without expanding `lutra.db` into backup
  content.

Example shape:

```yaml
environment:
  enabled: true
  schedule: "Sun *-*-* 01:00:00"
  targets: [app-files, nginx-config, systemd-units, app-uploads]
  acknowledge_plaintext: true
  exclude: ["*.token"]
  systemd_units: [nginx.service, myapp.service]
  docker_containers: [myapp]
  retention:
    max_count: 4
    max_age_days: 90
    mode: both
    keep_at_least: 1
```

- Every name in `targets` must resolve to exactly one configured `FileTarget` or
  `VolumeTarget`; database targets are rejected.
- `environment.enabled: true` requires `acknowledge_plaintext: true` and at least
  one referenced target. `config validate --preflight` verifies target
  references, source readability, service/container name syntax, and output
  directory permissions.
- Environment file payloads always combine target excludes, environment-level
  excludes, and a non-overridable built-in denylist for common secret material:
  `.env`, `.env.*`, `*.key`, `*.pem`, `*.p12`, `*.pfx`, `.ssh`, `credentials*`, and
  `secrets*`. The backup report lists excluded logical paths without values.
- Named volume content cannot be classified reliably. Referencing a volume is
  covered by the explicit plaintext acknowledgement and documentation warning;
  operators must not select volumes used as credential stores.

### Versioned recovery-set format

- Version 1 is one `environment_<UTC>_<id>.tar.gz` artifact under
  `<backup_directory>/environment/`, accompanied by the existing SHA-256
  sidecar convention and a JSON descriptor.
- The descriptor contains format version, artifact ID, creation time, artifact
  name/size/SHA-256, Lutra version, source names/kinds, and completion status.
  It must not contain source file contents, environment values, or raw command
  output.
- The archive contains:

```text
manifest.json
inventory/inventory.json
inventory/inventory.md
payload/files/<target>.tar.gz
payload/volumes/<target>.tar.gz
reports/backup.json
MISSING_SECRETS.md
RESTORE.md
```

- `manifest.json` uses integer `format_version: 1` and contains artifact ID,
  UTC timestamps, producing Lutra version, normalized source entries, payload
  relative paths, payload sizes and SHA-256 values, required tools, declared services/containers, restore
  ordering, and whether each source was classified sensitive.
- JSON serialization is deterministic: stable source ordering, UTC ISO-8601
  values, snake_case properties, and no environment-dependent absolute staging
  paths. Reject unknown future major format versions during inspect/restore.
- Each payload member remains a standard tar archive so the format can be
  inspected with tar without Lutra. The outer set is the unit of retention,
  checksum verification, and restore.

### Plaintext and secret boundary

- Recovery sets are plaintext and must be created with mode `0600` inside a
  `0700` environment directory. Temporary payloads use a private `0700`
  directory on the backup filesystem, never shared `/tmp`, and are deleted in
  `finally`.
- Built-in and configured exclusions reduce accidental secret capture but are
  not a secret scanner. The explicit acknowledgement, warnings, generated
  `MISSING_SECRETS.md`, and documentation must make that limitation visible.
- `environment restore` verifies the adjacent checksum before applying. This is
  transport integrity, not sender authentication; operators must obtain the set
  from their restricted backup storage.
- Restore never executes scripts or command strings embedded in an artifact.
  Service operations come from typed, validated manifest fields and require an
  explicit activation option.
- No log, exception, notification, descriptor, inventory, or console output may
  include secret contents or environment values. Tests use sentinel secrets and
  search every emitted surface and archive entry.

### Inventory and failure policy

- Split inventory collection from rendering so one typed snapshot produces
  both JSON and Markdown. Add OS release/kernel/architecture and tool versions;
  retain Docker container/image/network/volume and systemd/package/firewall
  coverage. Record collector status, exit code, and sanitized error per section.
- Required collectors for an enabled environment backup are `os`, `packages`,
  `docker`, and `systemd`. Missing tools may be represented as not applicable
  only when the configured sources do not need them. Any other required
  collector failure fails the environment backup instead of creating a falsely
  complete recovery set. Optional cron/firewall collection remains explicit.
- Docker inspect output must be transformed into a typed allowlist. Include
  image references/digests, mounts, restart policy, ports, network names, and
  environment variable names, but never environment values or labels that match
  secret-key patterns.
- Inventory is evidence and prerequisite guidance, not an instruction to
  automatically install all recorded packages or recreate arbitrary containers.

### Restore safety and idempotency

- `environment restore` defaults to an inspect/preflight plan and changes
  nothing. Applying requires `--apply`, a valid checksum sidecar, and interactive
  confirmation unless `--yes` is supplied.
- Support `--root <PATH>` for test/disaster staging. The default `/` requires a
  privileged process. All archive paths are normalized beneath the selected
  root; reject absolute entries, `..`, device nodes, unsafe links, duplicate
  normalized destinations, case-collisions, and writes into Lutra's state or
  backup directories.
- Before applying, decrypt into a private directory, validate the full manifest,
  verify every payload checksum, inspect every nested tar, check free space and
  required tools, and emit the complete ordered action plan. No destination is
  modified if preflight fails.
- Apply regular files through same-filesystem temporary paths and atomic rename
  where possible, preserve declared mode/owner/group when privileged, and report
  metadata that could not be restored. Existing files that differ are copied to
  a timestamped rollback directory unless `--no-rollback-copy` is explicitly
  supplied. Identical files are skipped.
- Docker volumes are restored only after configured consumers are stopped and
  only with `--include-volumes`; this remains destructive and is clearly listed
  in confirmation output. Re-running produces identical contents.
- Services and containers remain stopped by default. `--activate-services`
  enables/starts only declared units/containers after `systemctl daemon-reload`
  and typed validations such as `systemd-analyze verify` and `nginx -t` when
  relevant executables/config are present. A failed validation prevents
  activation and returns nonzero.
- A structured JSONL operation log under
  `<state_directory>/logs/environment/<operation-id>.jsonl` records start/end,
  sanitized step name, status, duration, and error category. Each run ends with
  a summary event even on cancellation. Apply logs distinguish changed,
  unchanged, skipped, rolled back, and failed actions.

### Retention, storage, and access

- Apply the environment-specific retention policy only to complete triples
  (artifact, checksum, public descriptor). Incomplete temporary files are never
  promoted and are cleaned on the next run after a conservative age threshold.
- Plaintext recovery sets are excluded from built-in full-root and target sync
  in this implementation. Offsite transfer is a deliberate manual operation to
  restricted storage until encryption exists. Document `0600` local files,
  `0700` directories, and the consequences of copying the set elsewhere.
- Never include `lutra.db`, WAL/SHM, operation logs, backup
  output directories, or rollback directories in the recovery set. Detect and
  reject recursive source paths under `backup_directory` or `state_directory`.

## Implementation phases

### Phase 1: Define the format and safe archive foundation

1. Add `EnvironmentBackupConfig` and YAML loading/validation for target
   references, sensitivity classification, services, schedule, and retention.
2. Add version-1 manifest/public-descriptor models under a new
   `Lutra.Core/Recovery` namespace, with deterministic JSON serialization and
   strict readers that reject unsupported versions and malformed members.
3. Refactor `FileArchive` only as needed to support writing to a supplied stream
   while preserving the existing file-target API and behavior.
4. Add a recovery-set writer/reader with atomic output and restrictive
   permissions that prevents traversal, unsafe tar entries,
   duplicate destinations, and secret leakage into
   the descriptor.
5. Add focused format, deterministic serialization, cancellation, malformed
   archive, traversal/link/device, permissions, and sentinel-secret tests.
6. Run the full test suite, both Linux single-file publishes, package audit, and
   `git diff --check`; record results and STOP.

**Phase 1 gate**: a fixture recovery set can be written atomically, parsed as
format version 1, and rejected when unsafe; restrictive modes and mandatory
secret exclusions are proven and existing backup/restore tests remain unchanged.

**Phase 1 verification (2026-08-05)**:

- `dotnet test Lutra.slnx --configuration Release`: 76 passed, 0 failed.
- Version-1 archive tests cover atomic promotion, payload checksums, cancellation
  cleanup, unsupported versions/kinds, path traversal, link rejection,
  deterministic descriptor JSON, and Linux `0700`/`0600` modes.
- Configuration tests cover explicit plaintext acknowledgement, file-target
  references, and database-target rejection. A streamed file archive test proves
  `.env` and private-key denylist entries are omitted while normal files remain.
- Linux x64 and arm64 self-contained publishes each contain one executable and
  no standalone `libe_sqlite3.so`; the package audit reports no vulnerabilities.
- Independent Codex review found a temporary-file permission race. Private files
  and directories are now created with restrictive Unix modes atomically; the
  complete test and publish gates passed after the correction.
- `git diff --check` passed.

### Phase 2: Implement coherent environment backup operations

1. Refactor inventory into typed collectors and JSON/Markdown renderers. Add OS,
   kernel, architecture, tool-version, image, and restart-policy coverage with
   the required/optional collector failure policy.
2. Implement `EnvironmentBackupService`: acquire one environment lock, resolve
   sources in stable order, capture fresh file/volume payloads, compute member
   checksums, collect inventory, generate restore guidance/report, stream the
   recovery set, verify it, atomically promote all sidecars, and finalize the
   `@environment` history operation.
3. Ensure any failed source, required inventory collector, archive write,
   verification, or promotion fails the complete operation. Never publish a
   partial set or delete an earlier valid set.
4. Add `lutra environment backup`, service-factory wiring, concise progress and
   errors, sanitized notifications if environment operations are notified, and
   environment-specific retention.
5. Install/remove/list a `lutra-environment-backup` systemd timer when enabled.
   Use the same resolved config/env paths and execution-identity rules as other
   scheduled commands.
6. Extend config templates, `lutra.example.yaml`, validation, backup directory
   exclusions, and full-root sync tests. Do not alter target-specific sync.
7. Test coherent success, every failure boundary, lock contention, cancellation,
   retention triples, stale temp cleanup, history state, permissions, schedule
   generation, source recursion rejection, collector sanitization, and absence
   of sentinel secrets in logs/output/descriptor/archive.
8. Run all gates, record results, and STOP.

**Phase 2 gate**: one scheduled or manual command creates a complete plaintext,
checksummed, versioned recovery set and clear sanitized logs; failures leave no
published partial artifact and retention cannot separate its sidecars.

### Phase 3: Implement guarded and idempotent restore

1. Implement `environment inspect` with format summary, transport checksum
   status, inventory summary, source list, and explicit plaintext warning.
2. Implement restore preflight: checksum verification, manifest
   validation, inner checksums, safe nested-tar inspection, destination conflict
   detection, free-space/tool/privilege checks, and a deterministic action plan.
3. Implement file application beneath `--root`, rollback copies, atomic writes,
   metadata restoration, unchanged-file skipping, and clear exceptions for
   non-idempotent metadata or platform behavior.
4. Add opt-in Docker volume restore with consumer stop checks. Add opt-in typed
   service validation/activation without accepting executable archive content.
5. Add `lutra environment restore` settings and confirmations. Ensure Ctrl+C
   stops before the next action, records cancellation, and leaves rollback data
   and a precise resume report.
6. Add tests for dry-run immutability, missing/bad checksum, corrupted members,
   malicious archives, privilege/tool failures, changed/unchanged files,
   rollback copies, repeated restore, volume opt-in, service activation opt-in,
   failed validation, cancellation, and complete sanitized JSONL logs.
7. Run all gates, record results, and STOP.

**Phase 3 gate**: a valid set restores twice to a disposable root with the
second run reporting no content changes; untrusted, corrupt, unsafe, or
incomplete sets cannot modify the destination, volumes, or service state.

### Phase 4: Prove reconstruction from a clean environment

1. Add `tests/e2e/environment-recovery.sh` (or an equivalent test harness) that
   creates a representative source environment with application files, NGINX
   config, systemd unit, excluded sentinel secret, and persistent non-database data; builds a
   set; then restores it into a clean supported Ubuntu/Debian environment.
2. The drill installs only documented prerequisites, runs preflight and apply,
   validates file contents/modes, verifies the sentinel secret was not archived,
   validates NGINX/systemd configuration, checks persistent data, and reruns
   restore to prove convergence.
3. Add a destructive-volume integration case where CI permits Docker. If CI
   cannot safely validate real systemd activation, test activation through the
   host adapter and keep a documented manual disposable-VM gate; do not claim a
   chroot or mocked service manager proves real activation.
4. Add the non-privileged fixture drill to Ubuntu CI and publish sanitized test
   logs on failure. Document the privileged disposable-VM drill and record its
   result before marking the phase complete.
5. Update `README.md`, `docs/commands.md`, `docs/configuration.md`,
   `docs/operations.md`, `docs/security.md`, and `docs/development.md` with the
   format, trust model, prerequisites, backup/restore runbook, idempotency
   exceptions, storage/retention/access policy, recovery ordering, testing, and
   the boundary between environment restore, database restore, and `bundle`.
6. Run the full suite, clean-environment drill, package audit, x64/arm64
   single-file publishes, CLI smoke tests, `bash -n` on shell scripts, secret
   sentinel scans, and `git diff --check`.
7. Mark the phase `Done`/`Pending`, set Plan 002 to `DONE` in
   `plans/README.md`, append final verification evidence, and STOP for review.

**Phase 4 gate**: another administrator can follow only the documented runbook
to prepare a clean supported host, verify and restore a recovery set,
validate operational configuration and persistent non-database data, and rerun
the process without unintended changes.

## Verification matrix

| Purpose | Command | Expected result |
|---|---|---|
| Full tests | `dotnet test Lutra.slnx --configuration Release` | exit 0; baseline and new tests pass |
| Clean recovery drill | `bash tests/e2e/environment-recovery.sh` | exit 0; second restore converges |
| x64 publish | `dotnet publish src/Lutra.CLI/Lutra.CLI.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /tmp/lutra-env-x64` | one executable, exit 0 |
| arm64 publish | `dotnet publish src/Lutra.CLI/Lutra.CLI.csproj -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o /tmp/lutra-env-arm64` | one executable, exit 0 |
| Package audit | `dotnet list src/Lutra.Core/Lutra.Core.csproj package --include-transitive --vulnerable` | no vulnerable packages |
| State exclusion | `rg -n 'lutra\.db|lutra\.db-wal|lutra\.db-shm' tests/e2e/fixtures/environment` | no recovery payload fixture includes state files |
| Secret leakage | search archive, descriptor, logs, stdout, stderr, notifications, and inventory for the test sentinel | no matches |
| Shell validation | `bash -n setup.sh tests/e2e/environment-recovery.sh` | exit 0 |
| Diff hygiene | `git diff --check` | exit 0 |

## Done criteria

- [ ] All phase rows are `Done` and each checkpoint review is recorded.
- [ ] `plans/README.md` marks Plan 002 `DONE`.
- [ ] Recovery-set format version 1 is documented and independently inspectable.
- [ ] Application files, service configuration, and selected
      non-database persistent data are represented by fresh, checksummed payloads.
- [ ] The inventory covers OS/packages/versions, containers/images, volumes,
      networks, services, and configured optional firewall/cron evidence.
- [ ] Plaintext scope and exclusions are explicit; no known secret or test
      sentinel is stored in an artifact, sidecar, inventory, or log.
- [ ] Restore requires a valid checksum, rejects unsafe content,
      changes nothing on failed preflight, and does not execute archive scripts.
- [ ] Applying the same set twice converges; documented exceptions are explicit.
- [ ] Every backup and restore step has a sanitized status, duration, and clear
      failure result, including cancellation.
- [ ] Retention and access policy are implemented and documented; incomplete
      triples are never published.
- [ ] `lutra.db`, WAL/SHM, logs, rollback copies, and backup
      output directories are never included.
- [ ] A reproducible clean-environment test and privileged disposable-host
      runbook demonstrate reconstruction without author-only knowledge.
- [ ] Existing database/file/volume backup, restore, bundle, inventory, sync,
      history, and release behavior remains compatible.

## STOP conditions

Stop and report instead of improvising if any of these occurs:

- Plan 001 is not complete or its final state/history/storage contracts differ
  materially from the evidence used here.
- Product requirements imply automatic package installation, user/SSH creation,
  firewall mutation, cloud provisioning, or arbitrary script execution. Those
  require separate threat modeling and explicit scope approval.
- A requirement emerges to include, snapshot, sync, or restore `lutra.db` or its
  WAL/SHM files. Application-state export remains a separate product/security
  decision.
- A selected source is known to contain secrets that cannot be excluded. Move
  those values to an external secret service or defer that source until an
  encrypted follow-up; do not silently archive it.
- Correct restore requires following symlinks outside `--root`, accepting device
  nodes, or executing commands supplied by the artifact.
- Docker volume consistency requires stopping a workload that Lutra cannot
  identify or coordinate safely. Report the application-specific quiescence
  requirement rather than claiming a consistent backup.
- A real clean-host validation cannot be run. Keep the phase incomplete and
  report exactly which privileged behavior remains unproven.
- Any phase gate fails twice after a reasonable scoped correction, or work from
  a later phase is required to make the current checkpoint safe.

## Maintenance notes

- Treat `format_version` as an external compatibility contract. Future changes
  add readers/migrations or a new major version; they do not silently reinterpret
  version 1.
- Keep manifest parsing strict and archive handling hostile-input safe even when
  the artifact decrypted successfully.
- New inventory fields must pass the secret review and sentinel tests before
  inclusion. Prefer typed allowlists over serializing raw command output.
- Preserve the distinction between plaintext access control, transport
  integrity, and authenticity in CLI messages and documentation.
- Restore logs are operational evidence, not a secret store. Paths may be
  sensitive; log logical source names by default and reveal absolute paths only
  in an explicit local verbose mode.

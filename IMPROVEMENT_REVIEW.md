# Lutra Improvement Review

## Available Commands

From `README.md` and `src/Lutra.CLI/Program.cs`, the current CLI surface is:

1. `lutra backup run [--target <name>]`
2. `lutra backup list`
3. `lutra history [--target <name>]`
4. `lutra cleanup [--target <name>]`
5. `lutra config init`
6. `lutra config validate`
7. `lutra config reset`
8. `lutra schedule install [--target <name>]`
9. `lutra schedule list`
10. `lutra schedule remove [--target <name>]`
11. `lutra uninstall`

### Planned But Not Implemented

1. `lutra restore`
2. `lutra cleanup --dry-run`

## Findings

### 1. Concurrent runs can lose or corrupt backup history

File refs:
- `src/Lutra.Core/History/BackupHistoryService.cs`
- `src/Lutra.Core/History/IBackupHistoryService.cs`

Why it matters:
`backup-history.json` uses a read-modify-write flow with no locking. A manual run overlapping with a timer-triggered run can overwrite or lose history entries, which also weakens retention behavior.

### 2. Overlapping SQL Server backups can overwrite each other inside the container

File ref:
- `src/Lutra.Core/Backup/SqlServerBackupProvider.cs`

Why it matters:
All SQL Server backups use the same temp path: `/tmp/lutra_backup.bak`. Two overlapping backups against the same container can clobber each other.

### 3. Same-target concurrent runs can collide on host filenames

File ref:
- `src/Lutra.Core/Backup/BackupOrchestrator.cs`

Why it matters:
Backup filenames only include timestamps with second-level precision. Two runs for the same target in the same second can produce the same output path.

### 4. Failed backups can leave partial files behind

File ref:
- `src/Lutra.Core/Backup/BackupOrchestrator.cs`

Why it matters:
If a backup fails after the output file has been created, partial or truncated backup files can remain on disk and look valid, while history does not track them as usable artifacts.

### 5. `config validate` is weaker than the README implies

File refs:
- `src/Lutra.CLI/Commands/Config/ConfigValidateCommand.cs`
- `src/Lutra.Core/Configuration/YamlConfigLoader.cs`
- `README.md`

Why it matters:
Current validation does not check:
- invalid schedule syntax
- duplicate target names
- invalid retention values
- missing referenced environment variables
- required credentials for target types
- permissions on config and backup directories
- whether containers and dump tools actually exist

### 6. The setup script generates cron syntax, but scheduling expects systemd calendar syntax

File refs:
- `setup.sh`
- `src/Lutra.CLI/Commands/Schedule/ScheduleInstallCommand.cs`
- `lutra.example.yaml`
- `README.md`

Why it matters:
The generated setup template uses cron-style values like `0 3 * * *`, but the scheduler writes those directly into `OnCalendar=...`, which expects systemd calendar expressions.

### 7. MongoDB passwords are passed as command-line arguments

File refs:
- `src/Lutra.Core/Backup/MongoBackupProvider.cs`
- `src/Lutra.Core/Backup/IBackupProvider.cs`

Why it matters:
Passwords passed as CLI arguments can be exposed in process listings.

### 8. There is no restore or verification path

File refs:
- `README.md`
- `src/Lutra.CLI/Program.cs`
- `src/Lutra.Core/Backup/BackupOrchestrator.cs`

Why it matters:
A backup is only truly useful if it can be restored. Right now, success mainly means the dump command exited cleanly and a file was written.

### 9. There are no automated tests in the repository

Why it matters:
This tool manages operational and recovery workflows, so regressions in scheduling, cleanup, retention, or dump execution can be costly.

## Recommended Improvements

Prioritized by practical impact:

1. Add restore verification first
2. Add locking for backup execution and history writes
3. Add integrity artifacts such as checksums
4. Strengthen preflight and configuration validation
5. Delete partial files on failure and reconcile orphaned artifacts
6. Add failure notifications
7. Add offsite-sync-friendly metadata
8. Add deeper database-specific recovery features later

## Best Next Steps

### 1. Add `verify` or `test-restore`

Best value feature for confidence. A disposable-container restore test is more useful than producing more dump formats.

### 2. Add locking

Use:
- a per-target execution lock
- a shared history file lock

This reduces concurrency-related corruption and collisions.

### 3. Add checksum manifests

Generate a `.sha256` next to each backup and optionally record it in history. This fits well with VPS-to-local sync workflows.

### 4. Strengthen `config validate`

Add checks for:
- systemd schedule validity
- required env vars
- duplicate target names
- positive retention settings
- container existence
- dump tool existence inside the container
- file permissions

### 5. Add partial-file cleanup and reconciliation

On failure:
- remove incomplete output files

Later add a command to:
- find files without history entries
- find history entries whose files are missing

### 6. Add notifications

Send failure alerts through:
- email
- webhooks
- healthchecks
- Slack or similar services

### 7. Improve offsite workflow support

Even if Lutra keeps transfers external, it can still help by generating:
- backup manifests
- checksums
- expected file inventory

### 8. Add advanced recovery features later

Examples:
- PostgreSQL WAL / PITR support
- SQL Server differential or log backup support
- MongoDB oplog-aware backups

## Recommended Priority Order

If you want the highest-value path forward, I'd do this first:

1. `verify` / `test-restore`
2. history and backup locking
3. checksum manifests
4. stronger `config validate`
5. failure notifications

These improve real-world reliability more than adding more dump formats.

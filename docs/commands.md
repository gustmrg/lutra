# Commands

All commands accept these global options:

```text
--config <PATH>       Path to config file (default: /etc/lutra/lutra.yaml)
--env-file <PATH>     Path to .env file (default: /etc/lutra/.env)
```

User-only installations use `~/.config/lutra/lutra.yaml` and `~/.config/lutra/.env` by default.

## Backup

```bash
lutra backup run                             # Back up all configured targets
lutra backup run --target my-postgres        # Back up one target
lutra backup list                            # List configured targets and schedules
lutra backup verify-file --file <PATH>       # Verify checksum and manifest
lutra backup reconcile                       # Compare files, sidecars, and history
lutra backup reconcile --target my-postgres  # Reconcile one target
lutra backup reconcile --json                # Machine-readable report
```

## Restore and Verify

```bash
lutra restore                                # Interactive restore
lutra restore --target my-postgres --file <PATH> --force
lutra verify                                 # Verify the latest backup
lutra verify --target my-postgres --file <PATH>
```

`restore` is destructive. It replaces the contents of the configured database. `verify` restores into a temporary database and leaves the configured one untouched.

## History and Maintenance

```bash
lutra history                                # History for all targets
lutra history --target my-postgres           # History for one target
lutra cleanup                                # Apply retention policy
lutra cleanup --target my-postgres           # Clean one target
lutra cleanup --dry-run                      # Preview deletions
lutra cleanup --orphan-sidecars              # Remove sidecars without artifacts
lutra cleanup --orphan-files --force         # Remove untracked artifacts
lutra cleanup --prune-history                # Prune old operational records
```

History statuses have these meanings:

| Status | Meaning |
|---|---|
| `CREATING` | A backup artifact is being created under an active lease. |
| `VERIFYING` | A non-destructive verification is running. |
| `UPLOADING` | An offsite sync is running. |
| `OK` | The operation completed successfully. |
| `FAILED` | The operation completed with an error. |
| `CANCELLED` | Lutra observed cancellation and recorded it. |
| `INTERRUPTED` | A process stopped renewing its lease; completion is unknown. |

## Sync

```bash
lutra sync --dry-run                         # Preview an offsite rsync
lutra sync --target my-postgres              # Sync one target directory
lutra sync --validate                        # Validate SSH, rsync, and remote access
lutra sync --delete                          # Explicitly mirror local deletions
```

## Configuration

```bash
lutra config init                            # Create config directories and templates
lutra config validate                        # Validate configuration
lutra config validate --preflight            # Also check systemd, Docker, pg_dump, and sync
lutra config reset                           # Reset config files to template defaults
```

## Scheduling

```bash
sudo lutra schedule install                  # Install timers for all targets
sudo lutra schedule install --target my-postgres
sudo lutra schedule install --user backup    # Run the timers as a specific account
lutra schedule list                          # List timers and status
sudo lutra schedule remove                   # Remove all Lutra timers
sudo lutra schedule remove --target my-postgres
```

Timers run as the account that invoked `sudo`, or as the one given with `--user`. Run manual commands as that same account so backups and local state keep a single owner.

Schedules use systemd calendar expressions, not cron syntax. See [Configuration](configuration.md#schedules).

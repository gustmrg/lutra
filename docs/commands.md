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

`restore` is destructive. It replaces the configured database or file target contents. `verify` performs a non-destructive test restore where supported.

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
lutra health                                 # Analyze backup health
lutra health --target my-postgres            # Analyze one target
lutra health --json                          # Machine-readable health report
lutra inventory                              # Capture an inventory snapshot
```

## Sync and Disaster Recovery

```bash
lutra sync --dry-run                         # Preview an offsite rsync
lutra sync --target my-postgres              # Sync one target directory
lutra sync --validate                        # Validate SSH, rsync, and remote access
lutra sync --delete                          # Explicitly mirror local deletions
lutra bundle                                 # Bundle latest artifacts and instructions
lutra bundle --encrypt                       # Encrypt a bundle with the global age recipient
```

## Configuration

```bash
lutra config init                            # Create config directories and templates
lutra config validate                        # Validate configuration
lutra config validate --preflight            # Also check systemd, Docker, and dump tools
lutra config generate                        # Generate config from docker-compose.yml
lutra config reset                           # Reset config files to template defaults
```

## Scheduling

```bash
sudo lutra schedule install                  # Install timers for all targets
sudo lutra schedule install --target my-postgres
lutra schedule list                          # List timers and status
sudo lutra schedule remove                   # Remove all Lutra timers
sudo lutra schedule remove --target my-postgres
```

Schedules use systemd calendar expressions, not cron syntax. See [Configuration](configuration.md#schedules).

## Uninstall

```bash
sudo lutra uninstall
```

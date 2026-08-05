# SWE-75 Implementation Plan: Discord Backup Notifications

## Objective

Implement actionable Discord notifications for backup success and failure while
keeping notification delivery best-effort and making the notification domain easy
to extend with future providers. Discord is the only notification provider in this
scope; Slack and Telegram are explicitly deferred.

The implementation must satisfy these outcomes:

- A successful backup reports the target, dump/archive name, artifact size,
  destination, and duration.
- A failed backup reports the target, error, and Docker container when the target
  is container-backed.
- More than one Discord webhook can receive the same event.
- A failed, slow, or malformed webhook response never changes the backup result or
  command exit code.
- The unused generic JSON webhook contract is removed rather than maintained beside
  Discord.
- The unused Healthchecks.io integration is also removed; Lutra does not require a
  second external monitoring service for this feature.
- Adding another provider later requires a provider implementation and
  configuration wiring, not changes to backup orchestration.

Plan baseline: repository commit `b6d58e9`, 2026-08-05.

## Current State

- `src/Lutra.Core/Notifications/NotificationService.cs` directly posts one
  generic payload to every `notifications.webhooks` URL and optionally pings
  Healthchecks.io. It owns transport, payload creation, and dispatch in one class.
- The generic webhook endpoint is not currently used and overlaps Discord's purpose,
  but its arbitrary JSON payload is not accepted by Discord incoming webhooks.
- `src/Lutra.CLI/Commands/Backup/BackupRunCommand.cs` sends only one aggregate
  message after a run. It discards the file path, size, duration, per-target error,
  and target/container metadata needed by SWE-75.
- `BackupResult` already contains target name, file path, file size, duration, and
  error. `DatabaseTarget` contains the database and container names. No backup
  provider or orchestrator changes are required to obtain the requested data.
- Sync, health, verify, and restore also use `NotificationService`; their behavior
  must not regress while the service is refactored.
- Notification URL validation exists, but there are no notification-focused tests
  and the static `HttpClient` prevents deterministic HTTP tests.

## Scope

### In Scope

- Typed notification event and backup-detail models in `Lutra.Core`.
- A small notification-channel contract and a dispatcher that isolates failures
  per configured channel.
- A Discord channel using incoming webhook URLs.
- Multiple Discord webhook endpoints.
- Environment-variable-based Discord webhook secrets.
- Rich backup result mapping and Discord success/failure embeds.
- Removal of the generic JSON webhook and Healthchecks.io configuration, transports,
  examples, and documentation.
- Configuration validation, examples, user documentation, and focused tests.

### Out of Scope

- Slack, Telegram, email, or provider auto-detection from URL hostnames.
- Discord bots, OAuth, slash commands, inbound webhooks, or interactive messages.
- Retries, queues, persistence, delivery history, or guaranteed delivery.
- Notifications for backup progress or retention cleanup.
- Changing backup artifact formats, history schema, scheduling, or exit-code rules.
- Per-target notification configuration; notifications remain global.
- Backward compatibility for the unused `notifications.webhooks` setting.
- Backward compatibility for the unused `notifications.healthchecks_url` setting.

## Design Decisions

### 1. Separate Event Data, Dispatch, and Provider Formatting

Introduce these concepts under `src/Lutra.Core/Notifications/`:

- `NotificationEvent`: provider-neutral event metadata (`Event`, `Status`,
  `Summary`, `Target`, UTC timestamp, host) plus optional backup details.
- `BackupNotificationDetail`: one target's target kind, database name, container,
  success flag, file name, size, destination, duration, and error message.
- `INotificationChannel`: one asynchronous `SendAsync(NotificationEvent,
  CancellationToken)` method and a non-secret display name for diagnostics.
- `NotificationService`: dispatches an event to all channels, catches each
  channel's delivery failure independently, and returns sanitized warning strings.
- `DiscordNotificationChannel`: maps provider-neutral events to Discord JSON.

The dispatcher must not know Discord payload fields. Backup commands must not know
how any provider formats or sends messages. A future provider should implement
`INotificationChannel`, add a typed config section, and be composed in
`ServiceFactory`.

Do not introduce a general plugin loader, reflection-based provider discovery, or
dependency-injection framework for this feature.

### 2. Remove the Existing Outbound Integrations

Remove `NotificationConfig.Webhooks`, `NotificationPayload`, the generic JSON POST
loop, `NotificationConfig.HealthchecksUrl`, the Healthchecks.io GET logic, validation
for both settings, and their documentation. Do not retain legacy senders, provider
adapters, or compatibility aliases: neither integration is in use, and preserving
them would create overlapping configuration and an unnecessary external dependency.

Update configuration examples and release-facing documentation to state that
the old `notifications.webhooks` and `notifications.healthchecks_url` settings were
removed and Discord is the only supported notification integration. No production
code should emit the old `NotificationPayload` JSON or ping Healthchecks.io after
this change.

Backup execution, artifacts, local history, systemd status/logging, and
`lutra health` remain fully functional without network access or notification
configuration. Discord is strictly optional and best-effort. Detecting that an
entire host or timer never ran requires an external observer and is intentionally
left to infrastructure monitoring outside Lutra.

### 3. Discord Configuration and Secret Handling

Extend `NotificationConfig` with a Discord-specific section:

```yaml
notifications:
  discord:
    webhooks:
      - url_env: LUTRA_DISCORD_WEBHOOK_OPERATIONS
      - url_env: LUTRA_DISCORD_WEBHOOK_BACKUPS
```

Add the corresponding environment example:

```dotenv
LUTRA_DISCORD_WEBHOOK_OPERATIONS=https://discord.com/api/webhooks/...
LUTRA_DISCORD_WEBHOOK_BACKUPS=https://discord.com/api/webhooks/...
```

Each configured entry resolves its URL from the named environment variable after
the existing `.env` loader runs. Do not put Discord webhook URLs in generated YAML
because each URL contains a credential.

Validation in `YamlConfigLoader` must reject:

- A blank `url_env` name.
- A missing or blank referenced environment value.
- A resolved URL that is not absolute HTTPS.
- A resolved host other than `discord.com` or `discordapp.com`, including their
  subdomains, to prevent accidentally sending notification data elsewhere.
- A Discord config with no webhook entries.

Validation errors should name the configuration path and environment variable but
must never print the resolved webhook URL. Store the resolved URL only in the
runtime channel; diagnostic names and errors must not contain it.

### 4. Backup Event Mapping

After `BackupRunCommand` has all `BackupResult` values, map every result to one
`BackupNotificationDetail` by joining `result.TargetName` to `config.AllTargets()`.
Fail configuration loudly if a result cannot be matched, because that indicates an
internal consistency bug.

Map fields as follows:

| Notification field | Source |
|---|---|
| Target | `BackupResult.TargetName` |
| Target kind | `DatabaseTarget`, `FileTarget`, or `VolumeTarget` |
| Database | `DatabaseTarget.Database`, otherwise omitted |
| Container | `DatabaseTarget.Container`, otherwise omitted |
| Dump/archive | `Path.GetFileName(BackupResult.FilePath)`, otherwise omitted |
| Size | `BackupResult.FileSizeBytes`, otherwise omitted |
| Destination | `Path.GetDirectoryName(BackupResult.FilePath)`, otherwise omitted |
| Duration | `BackupResult.Duration` |
| Error | `BackupResult.ErrorMessage`, otherwise `Unknown error` on failure |

Keep the command-level event name and status behavior: all successful results use
`backup_success`; any failed result makes the event `backup_failure`. Attach all
per-target details to that one Discord event.

Do not add notification calls inside `BackupOrchestrator`. Notification concerns
belong at the CLI/application boundary after results are finalized, and keeping them
there guarantees webhook failures cannot invalidate artifacts or history records.

### 5. Discord Payload

Send Discord's incoming-webhook JSON format with `username` set to `Lutra` and one
embed per backup result.

Success embed:

- Title: `\u2705 Lutra Backup - SUCCESS`.
- Green color.
- Fields: target, database when present, dump/archive, human-readable size,
  destination, and duration.
- UTC event timestamp and host in the footer.

Failure embed:

- Title: `\u274c Lutra Backup - FAILED`.
- Red color.
- Fields: target, database when present, error, container when present, and
  duration.
- UTC event timestamp and host in the footer.

Discord accepts at most 10 embeds in one message. Partition details into stable
chunks of 10 and POST each chunk to every configured Discord webhook. Preserve the
result order emitted by `BackupRunCommand`.

Apply Discord's documented limits before serialization: truncate titles, field
names, field values, footer text, and total embed text with an explicit `...`
suffix. At minimum, error messages must be bounded so an unexpectedly large stderr
value cannot make the notification fail. Use plain text only; do not enable user or
role mentions (`allowed_mentions.parse` must be empty).

Treat HTTP 2xx, including Discord's usual 204 response, as success. For non-2xx
responses, return a sanitized error containing provider name and status code only;
do not log the URL or response body because either may contain sensitive data.

### 6. Failure Isolation and Observability

`NotificationService` must attempt all channels even when an earlier channel fails.
It returns one warning per failed channel/request instead of throwing delivery
errors to command callers. `BackupRunCommand` prints those warnings in yellow after
the backup result table but calculates its exit code only from backup and sync
results.

The same isolation applies to current sync, health, verify, and restore callers.
Update them to use the typed event API and print or otherwise surface returned
warnings without changing their existing exit codes.

Use a finite 10-second HTTP timeout. Cancellation or timeout during notification
delivery becomes a warning, not a backup failure. Do not retry in this version;
systemd will retain the warning in command logs, and retries could duplicate Discord
messages.

## Implementation Steps

### Phase 1: Introduce the Extensible Notification Core

1. Add `NotificationEvent`, `NotificationStatus`, and
   `BackupNotificationDetail` with immutable, provider-neutral fields.
2. Add `INotificationChannel` and refactor `NotificationService` into a dispatcher
   over a read-only channel collection.
3. Delete the generic JSON webhook sender, `NotificationPayload`, and
   `NotificationConfig.Webhooks` instead of carrying forward an unused contract.
4. Delete `NotificationConfig.HealthchecksUrl` and the Healthchecks.io GET behavior
   rather than keeping another external integration in Lutra's essential path.
5. Make HTTP transport injectable (an `HttpClient` or narrow handler dependency)
   so tests can capture requests without opening sockets. Production composition
   should share a bounded-timeout client rather than creating one per event.
6. Update `ServiceFactory.CreateNotificationService` to compose only channels that
   have configuration. Return `null` when no channels are configured.
7. Update current callers to send typed notification events and surface delivery
   warnings without changing operation results.

Phase gate: all existing tests pass; no production code emits the old generic JSON
payload or pings Healthchecks.io; one channel failure does not prevent another
channel from running.

### Phase 2: Add Discord Configuration and Provider

1. Add typed Discord configuration classes and the `notifications.discord.webhooks`
   collection of environment-variable references.
2. Resolve and validate Discord webhook URLs in the existing configuration load and
   validation path without retaining secrets in error messages.
3. Implement `DiscordNotificationChannel`, including payload DTOs, embed formatting,
   text limits, mention suppression, 10-embed chunking, and non-2xx handling.
4. Compose one Discord channel per configured webhook, or one channel over the URL
   collection, provided delivery errors remain attributable by a non-secret index.
5. Add configuration and channel tests for one/multiple endpoints, success/failure
   embeds, optional fields, chunking, timeout, and partial endpoint failure.

Phase gate: captured Discord requests conform to incoming-webhook JSON, no test or
diagnostic output contains a configured URL, and every configured endpoint is
attempted despite failures.

### Phase 3: Integrate Rich Backup Results

1. Add a focused mapper from `BackupResult` plus `IBackupTarget` to
   `BackupNotificationDetail`; keep formatting out of `BackupRunCommand`.
2. Change the backup notification call to include every result rather than only an
   aggregate sentence.
3. Verify mappings for database, file, volume, PostgreSQL WAL, mixed-success, and
   missing optional artifact fields.
4. Add command-level tests or a narrow extracted application test proving Discord
   notification failure leaves a successful backup exit code at 0 and backup
   failure remains exit code 1 regardless of notification outcome.
5. Confirm post-backup sync notifications remain separate events and are not
   mislabeled as backup results.

Phase gate: SWE-75 success and failure fields are present for each target, mixed
runs retain all result details, and notification transport cannot alter backup
artifacts, history, sync decisions, or exit status.

### Phase 4: Documentation and Final Verification

1. Update `lutra.example.yaml` with commented Discord configuration using
   `url_env`, remove the generic webhook and Healthchecks.io examples, and do not
   include a real or syntactically valid credential.
2. Update `.env.example` with a placeholder Discord webhook variable.
3. Update `docs/configuration.md` with the Discord schema, multiple webhook
   behavior, delivery timeout/best-effort semantics, field mapping, and removal of
   `notifications.webhooks` and `notifications.healthchecks_url`.
4. Update `docs/security.md` to classify Discord webhook URLs as credentials and
   recommend `.env` mode `600`, endpoint rotation, and restricted log sharing.
5. Run the full test suite and release build checks.

Phase gate: examples load after placeholder substitution, docs match runtime
configuration, and release output gains no new package/runtime dependency.

## Test Matrix

Add focused tests under `tests/Lutra.Core.Tests/Notifications/` (or the nearest
existing test convention) using a fake `HttpMessageHandler`:

- Dispatcher attempts every channel and returns sanitized errors independently.
- One Discord success result includes dump name, size, destination, and duration.
- One Discord failure includes error and database container.
- File and volume failures omit container instead of emitting an empty field.
- A mixed result set creates success and failure embeds in original order.
- 11 results produce two Discord requests with 10 and 1 embeds.
- Two configured webhooks both receive every chunk.
- HTTP 204 succeeds; HTTP 400/429/500 produce warnings without exceptions.
- Timeout and caller cancellation produce warnings without changing operation
  status.
- Long error/output text is truncated within Discord limits.
- `allowed_mentions` prevents all mentions.
- Missing env var, blank env var, HTTP Discord URL, and foreign host are rejected.
- Validation and runtime warnings never include webhook URLs.
- A configuration with no notification endpoints creates no notification service.
- No production request uses the old generic `NotificationPayload` schema.
- No production request pings a Healthchecks.io endpoint.

## Verification Commands

```bash
dotnet test Lutra.slnx --configuration Release
dotnet build Lutra.slnx --configuration Release
dotnet publish src/Lutra.CLI/Lutra.CLI.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained \
  -p:PublishSingleFile=true \
  -o /tmp/lutra-swe-75-publish
git diff --check
```

Perform one manual smoke test with a disposable Discord webhook:

1. Configure its URL through a temporary `.env` variable.
2. Run one successful file-target backup and verify all success fields.
3. Run one intentionally failing database target and verify error/container fields.
4. Replace the URL with an unreachable endpoint, rerun a successful backup, and
   confirm the artifact/history are successful and the process exits 0 with a
   notification warning.
5. Delete the disposable webhook after testing.

## Done Criteria

- [ ] Discord is the only notification provider implemented; Slack and Telegram
      code is not stubbed or added.
- [ ] Provider-neutral event models and `INotificationChannel` isolate backup logic
      from Discord formatting.
- [ ] Success notifications include dump/archive, size, destination, and duration.
- [ ] Failure notifications include error and container when applicable.
- [ ] Multiple Discord webhooks and more than 10 backup results are supported.
- [ ] The generic JSON webhook configuration, sender, payload, examples, and docs
      are removed.
- [ ] Healthchecks.io configuration, sender, examples, and docs are removed.
- [ ] Every delivery failure is visible but cannot alter operation exit status.
- [ ] Discord URLs are loaded from environment variables and never logged.
- [ ] Discord payload limits and mention suppression are enforced.
- [ ] Configuration examples and security documentation match implementation.
- [ ] Full tests, release build/publish, and `git diff --check` pass.

## Risks and Mitigations

- **Discord API limits change:** Keep Discord DTOs and constraints inside the
  Discord channel so updates do not affect event producers.
- **Large all-target runs exceed message limits:** Chunk at 10 embeds and bound all
  text fields before serialization.
- **Webhook latency delays command completion:** Use the existing finite timeout;
  document that notifications are synchronous best-effort delivery for now.
- **Secret leakage through validation/logging:** Reference environment variable
  names in YAML and sanitize all transport errors.
- **Refactor regresses non-backup notifications:** Exercise backup, sync, verify,
  restore, and health event mapping through the Discord channel.
- **Duplicate messages after retries:** Do not retry in this feature; surface a
  warning and leave retry policy for a future design with idempotency/delivery
  tracking.

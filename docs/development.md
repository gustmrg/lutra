# Development

## Requirements

- .NET 10.0 SDK
- Linux for the release binary and systemd integration
- Docker containers for integration and restore testing

## Build From Source

```bash
git clone https://github.com/gustmrg/lutra.git
cd lutra

dotnet publish src/Lutra.CLI/Lutra.CLI.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained \
  -p:PublishSingleFile=true \
  -o dist/

# The binary is at dist/Lutra.CLI.
sudo install -m 0755 dist/Lutra.CLI /usr/local/bin/lutra
```

Use `linux-arm64` instead for a 64-bit ARM server. Trimming is disabled because Spectre.Console.Cli uses reflection.

Run the automated tests with:

```bash
dotnet test Lutra.slnx
```

CI runs unit tests, a Linux-only multi-process CLI concurrency test, CLI smoke tests including legacy-history migration, the clean-environment reconstruction drill, and validates self-contained `linux-x64` and `linux-arm64` release archive layouts. Each release archive must contain only the `lutra` executable; the SQLite native provider is embedded. Database restore integrations remain opt-in/manual because they require disposable provider containers.

Run the environment drill with:

```bash
bash tests/e2e/environment-recovery.sh
LUTRA_E2E_VOLUME=1 bash tests/e2e/environment-recovery.sh
```

On Linux it publishes the current CLI and runs against a disposable filesystem.
On macOS it executes the non-volume path in a clean Ubuntu 24.04 container. CI
enables the destructive-volume case on an ephemeral Docker volume. The drill
uses only public CLI commands, independently opens the recovery archive, checks
secret and local-state exclusions, validates restored file contents/modes plus
NGINX/systemd configuration, and proves that a second apply converges. It does
not claim that a container validates real systemd activation; perform the
privileged activation steps from the operations runbook on a disposable VM or
host before a release that changes activation behavior.

## Project Structure

```text
Lutra/
├── src/
│   ├── Lutra.CLI/       # Entry point and Spectre.Console commands
│   └── Lutra.Core/      # Backup, restore, config, history, and integrations
├── tests/                # Unit tests and clean-environment drills
├── setup.sh              # Binary installation and initial configuration
├── lutra.example.yaml    # Annotated configuration example
├── .env.example          # Environment file example
├── Lutra.slnx
└── README.md
```

The CLI project owns command parsing, global settings, and service composition. The Core project has no UI dependency and contains database providers, file archives, integrity, retention, health, sync, bundles, configuration, and persistence logic.

## Application Database

`LutraDatabase` owns `<state_directory>/lutra.db`, short-lived configured connections, SQLite PRAGMAs, integrity checks, and ordered application migrations under `Persistence/Migrations`. Domain repositories such as `SqliteBackupHistoryRepository` own their tables and queries; future domains should add independent repositories and migrations instead of turning history or `app_metadata` into a generic store.

Migrations run transactionally and are recorded once in `schema_migrations`. SQLite uses WAL, `synchronous=FULL`, foreign keys, and a 30-second busy timeout. Tests cover idempotent migrations, ownership conflicts, rolled-back write probes, lifecycle transitions, import rollback, and concurrency. The Linux process test launches separate CLI processes because in-process tasks and macOS locking do not reproduce the systemd deployment model.

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 10.0 |
| CLI UI | Spectre.Console |
| Configuration | YamlDotNet |
| Scheduling | systemd timers |
| Backup execution | `docker exec` |
| Application state and history | SQLite (`Microsoft.Data.Sqlite`, WAL) |

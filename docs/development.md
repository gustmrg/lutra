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

CI runs unit tests, CLI smoke tests, and validates self-contained `linux-x64` and `linux-arm64` release archive layouts. Docker-based restore tests are opt-in/manual because they require disposable database containers.

## Project Structure

```text
Lutra/
├── src/
│   ├── Lutra.CLI/       # Entry point and Spectre.Console commands
│   └── Lutra.Core/      # Backup, restore, config, history, and integrations
├── tests/                # Automated tests
├── setup.sh              # Binary installation and initial configuration
├── lutra.example.yaml    # Annotated configuration example
├── .env.example          # Environment file example
├── Lutra.slnx
└── README.md
```

The CLI project owns command parsing, global settings, and service composition. The Core project has no UI dependency and contains database providers, file archives, integrity, retention, health, sync, bundles, and configuration logic.

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 10.0 |
| CLI UI | Spectre.Console |
| Configuration | YamlDotNet |
| Scheduling | systemd timers |
| Backup execution | `docker exec` |
| History | JSON file |

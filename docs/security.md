# Security

- Lutra opens no ports and listens on no sockets.
- Database passwords are resolved from environment variables rather than stored in YAML.
- Discord incoming webhook URLs are credentials. Keep them in `.env`, never in YAML, and rotate a webhook immediately if its URL is exposed.
- Keep `.env` files mode `600` and readable only by the account running Lutra.
- Keep age private identity keys off the VPS. Store only the public recipient in Lutra configuration.
- `config validate` warns about overly permissive configuration and backup directories.
- File targets can contain `.env` files and private keys. Configure age encryption for sensitive paths.
- Docker socket access is equivalent to significant host access. Restrict membership in the `docker` group.
- Use a restricted remote user and a pull-based job for stronger isolation when copying backups offsite.
- Lutra inventory snapshots intentionally omit Docker environment values and cron commands to avoid recording embedded credentials.
- Keep `state_directory` on a local filesystem. SQLite WAL on NFS or a multi-host shared directory is unsupported.
- Run scheduled and manual commands as the same OS account. That account needs private read/write access to the state directory and its `lutra.db`, `lutra.db-wal`, and `lutra.db-shm` family.
- Treat the application database as potentially sensitive operational metadata. Restrict its directory to the Lutra execution account.
- Lutra never implicitly exports, snapshots, bundles, or syncs its live application database. Back up artifacts separately; copying a live WAL database family is not a supported state backup.
- Discord delivery errors are sanitized so logs do not contain webhook URLs or response bodies. Continue to restrict access to Lutra configuration and service logs.

See [Configuration](configuration.md#encryption) for age encryption and [Operations](operations.md#offsite-copies) for offsite-copy recommendations.

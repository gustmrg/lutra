# Security

- Lutra opens no ports and listens on no sockets.
- Database passwords are resolved from environment variables rather than stored in YAML, and reach `docker exec` through the environment, never as a command-line argument.
- Discord incoming webhook URLs are credentials. Keep them in `.env`, never in YAML, and rotate a webhook immediately if its URL is exposed.
- Keep `.env` files mode `600` and readable only by the account running Lutra.
- `config validate` warns about overly permissive configuration and backup directories.
- Backups are not encrypted. Restrict the backup directory and the sync destination to the account that runs Lutra; anyone who can read a dump can read the database.
- Docker socket access is equivalent to significant host access. Restrict membership in the `docker` group.
- Use a restricted remote user and a pull-based job for stronger isolation when copying backups offsite.
- Keep `state_directory` on a local filesystem. SQLite WAL on NFS or a multi-host shared directory is unsupported.
- Run scheduled and manual commands as the same OS account. That account needs private read/write access to the state directory and its `lutra.db`, `lutra.db-wal`, and `lutra.db-shm` family.
- Treat the application database as potentially sensitive operational metadata. Restrict its directory to the Lutra execution account.
- Lutra never implicitly exports, snapshots, or syncs its live application database. Back up artifacts separately; copying a live WAL database family is not a supported state backup.
- Discord delivery errors are sanitized so logs do not contain webhook URLs or response bodies. Continue to restrict access to Lutra configuration and service logs.

See [Operations](operations.md#offsite-copies) for offsite-copy recommendations.

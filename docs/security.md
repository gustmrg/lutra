# Security

- Lutra opens no ports and listens on no sockets.
- Database passwords are resolved from environment variables rather than stored in YAML.
- Keep `.env` files mode `600` and readable only by the account running Lutra.
- Keep age private identity keys off the VPS. Store only the public recipient in Lutra configuration.
- `config validate` warns about overly permissive configuration and backup directories.
- File targets can contain `.env` files and private keys. Configure age encryption for sensitive paths.
- Docker socket access is equivalent to significant host access. Restrict membership in the `docker` group.
- Use a restricted remote user and a pull-based job for stronger isolation when copying backups offsite.
- Lutra inventory snapshots intentionally omit Docker environment values and cron commands to avoid recording embedded credentials.

See [Configuration](configuration.md#encryption) for age encryption and [Operations](operations.md#offsite-copies) for offsite-copy recommendations.

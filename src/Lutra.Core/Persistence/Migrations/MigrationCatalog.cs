namespace Lutra.Core.Persistence.Migrations;

internal static class MigrationCatalog
{
    public static IReadOnlyList<LutraMigration> All { get; } =
    [
        new(1, "001_application_database", """
            CREATE TABLE app_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """),
        new(2, "002_backup_operations", """
            CREATE TABLE backup_operations (
                id TEXT PRIMARY KEY CHECK (length(id) = 32),
                target_name TEXT NOT NULL CHECK (length(target_name) > 0),
                operation_type TEXT NOT NULL CHECK (operation_type IN ('backup', 'verify', 'sync')),
                status TEXT NOT NULL CHECK (status IN (
                    'creating', 'verifying', 'uploading',
                    'succeeded', 'failed', 'cancelled', 'interrupted'
                )),
                started_at_unix_ms INTEGER NOT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                completed_at_unix_ms INTEGER NULL,
                lease_id TEXT NULL,
                lease_expires_at_unix_ms INTEGER NULL,
                file_name TEXT NULL,
                file_size_bytes INTEGER NULL CHECK (file_size_bytes IS NULL OR file_size_bytes >= 0),
                sha256 TEXT NULL,
                manifest_file_name TEXT NULL,
                duration_ms INTEGER NULL CHECK (duration_ms IS NULL OR duration_ms >= 0),
                error_message TEXT NULL,
                CHECK (
                    (status IN ('creating', 'verifying', 'uploading')
                        AND completed_at_unix_ms IS NULL
                        AND lease_id IS NOT NULL
                        AND lease_expires_at_unix_ms IS NOT NULL)
                    OR
                    (status IN ('succeeded', 'failed', 'cancelled', 'interrupted')
                        AND completed_at_unix_ms IS NOT NULL
                        AND lease_id IS NULL
                        AND lease_expires_at_unix_ms IS NULL)
                )
            );

            CREATE INDEX ix_backup_operations_target_started
                ON backup_operations (target_name, started_at_unix_ms DESC);
            CREATE INDEX ix_backup_operations_status_lease
                ON backup_operations (status, lease_expires_at_unix_ms);
            """)
    ];
}

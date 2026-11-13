namespace Lutra.Core.Configuration;

/// <summary>
/// Specifies the type of database to back up.
/// </summary>
public enum DatabaseType
{
    /// <summary>
    /// PostgreSQL database. Uses <c>pg_dump</c> for backups.
    /// </summary>
    PostgreSql
}

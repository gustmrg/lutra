namespace Lutra.Core.Configuration;

/// <summary>
/// Specifies the type of database to back up.
/// </summary>
public enum DatabaseType
{
    /// <summary>
    /// PostgreSQL database. Uses <c>pg_dump</c> for backups.
    /// </summary>
    PostgreSql,

    /// <summary>
    /// Microsoft SQL Server database. Uses <c>sqlcmd</c> with <c>BACKUP DATABASE</c>.
    /// </summary>
    SqlServer,

    /// <summary>
    /// MongoDB database. Uses <c>mongodump</c> for backups.
    /// </summary>
    MongoDb
}

using Microsoft.Data.Sqlite;
using Lutra.Core.Persistence.Migrations;

namespace Lutra.Core.Persistence;

/// <summary>Owns Lutra's application database, connections, and schema migrations.</summary>
public sealed class LutraDatabase
{
    private const string ConfigPathMetadataKey = "installation.config_path";
    private readonly string _connectionString;
    private readonly string _normalizedConfigPath;
    private readonly string? _legacyBackupDirectory;

    public LutraDatabase(string stateDirectory, string configPath, string? legacyBackupDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        StateDirectory = Path.GetFullPath(stateDirectory);
        DatabasePath = Path.Combine(StateDirectory, "lutra.db");
        _normalizedConfigPath = Path.GetFullPath(configPath);
        _legacyBackupDirectory = legacyBackupDirectory is null
            ? null
            : Path.GetFullPath(legacyBackupDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public string StateDirectory { get; }

    public string DatabasePath { get; }

    /// <summary>Creates the database and applies every pending application migration.</summary>
    public void Initialize(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(StateDirectory);

        using var connection = OpenConnection(cancellationToken);
        using (var journalModeCommand = connection.CreateCommand())
        {
            journalModeCommand.CommandText = "PRAGMA journal_mode = WAL;";
            cancellationToken.ThrowIfCancellationRequested();
            var journalMode = Convert.ToString(journalModeCommand.ExecuteScalar());
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite WAL mode is required for '{DatabasePath}', but SQLite selected '{journalMode}'.");
            }
        }

        var transactionStarted = false;
        try
        {
            ExecuteNonQuery(connection, "BEGIN IMMEDIATE;", cancellationToken);
            transactionStarted = true;
            ExecuteNonQuery(connection, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    applied_at_utc TEXT NOT NULL
                );
                """, cancellationToken);

            foreach (var migration in MigrationCatalog.All.OrderBy(item => item.Version))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MigrationWasApplied(connection, migration.Version, cancellationToken))
                    continue;

                ExecuteNonQuery(connection, migration.Sql, cancellationToken);
                using var ledgerCommand = connection.CreateCommand();
                ledgerCommand.CommandText = """
                    INSERT INTO schema_migrations (version, name, applied_at_utc)
                    VALUES ($version, $name, $appliedAtUtc);
                    """;
                ledgerCommand.Parameters.AddWithValue("$version", migration.Version);
                ledgerCommand.Parameters.AddWithValue("$name", migration.Name);
                ledgerCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
                cancellationToken.ThrowIfCancellationRequested();
                ledgerCommand.ExecuteNonQuery();
            }

            ValidateOrClaimConfigOwnership(connection, cancellationToken);
            if (_legacyBackupDirectory is not null)
                LegacyBackupHistoryImporter.ImportIfNeeded(
                    connection, _legacyBackupDirectory, cancellationToken);
            ExecuteNonQuery(connection, "COMMIT;", cancellationToken);
            transactionStarted = false;
        }
        catch
        {
            if (transactionStarted)
            {
                try
                {
                    ExecuteNonQuery(connection, "ROLLBACK;", CancellationToken.None);
                }
                catch (SqliteException)
                {
                    // Preserve the original migration/validation error.
                }
            }

            throw;
        }
    }

    /// <summary>Runs SQLite's built-in integrity check.</summary>
    public string CheckIntegrity(CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    /// <summary>Verifies that the main database and its WAL family are writable.</summary>
    /// <remarks>The schema probe is rolled back and leaves no application object behind.</remarks>
    public void ProbeWriteAccess(CancellationToken cancellationToken = default)
    {
        Initialize(cancellationToken);
        using var connection = OpenConnection(cancellationToken);
        var transactionStarted = false;
        try
        {
            ExecuteNonQuery(connection, "BEGIN IMMEDIATE;", cancellationToken);
            transactionStarted = true;
            ExecuteNonQuery(
                connection,
                $"CREATE TABLE \"__lutra_write_probe_{Guid.NewGuid():N}\" (value INTEGER NOT NULL);",
                cancellationToken);
            ExecuteNonQuery(connection, "ROLLBACK;", cancellationToken);
            transactionStarted = false;
        }
        finally
        {
            if (transactionStarted)
            {
                try
                {
                    ExecuteNonQuery(connection, "ROLLBACK;", CancellationToken.None);
                }
                catch (SqliteException)
                {
                    // Preserve a probe failure while making a best effort to release the write lock.
                }
            }
        }
    }

    /// <summary>Opens a short-lived configured connection for a domain repository.</summary>
    public SqliteConnection OpenConnection(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            ExecuteNonQuery(connection, "PRAGMA busy_timeout = 30000;", cancellationToken);
            ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;", cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool MigrationWasApplied(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private void ValidateOrClaimConfigOwnership(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO app_metadata (key, value)
                VALUES ($key, $value);
                """;
            insert.Parameters.AddWithValue("$key", ConfigPathMetadataKey);
            insert.Parameters.AddWithValue("$value", _normalizedConfigPath);
            cancellationToken.ThrowIfCancellationRequested();
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT value FROM app_metadata WHERE key = $key;";
        select.Parameters.AddWithValue("$key", ConfigPathMetadataKey);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = Convert.ToString(select.ExecuteScalar());
        if (!string.Equals(owner, _normalizedConfigPath, StringComparison.Ordinal))
        {
            throw new LutraDatabaseOwnershipException(
                StateDirectory,
                owner,
                _normalizedConfigPath);
        }
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}

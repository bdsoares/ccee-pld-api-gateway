using Microsoft.Data.Sqlite;
using Ccee.PldApp.Infrastructure.Configuration;

namespace Ccee.PldApp.Infrastructure.Persistence;

/// <summary>
/// Creates the SQLite schema used to persist cached PLD queries and records.
/// </summary>
public sealed class SqliteDatabaseInitializer
{
    private readonly PldGatewayOptions _options;

    public SqliteDatabaseInitializer(PldGatewayOptions? options = null)
    {
        _options = options ?? new PldGatewayOptions();
    }

    /// <summary>
    /// Ensures the SQLite database and required tables exist before the gateway starts serving requests.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = _options.ResolveDatabasePath();
        var directory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            -- Query metadata is stored separately from the hourly records so the cache can be replayed quickly.
            CREATE TABLE IF NOT EXISTS pld_query_cache (
                query_key TEXT PRIMARY KEY,
                resource_id TEXT NOT NULL,
                day TEXT NULL,
                submarket TEXT NULL,
                limit_value INTEGER NOT NULL,
                used_month_reference_fallback INTEGER NOT NULL,
                retrieved_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS pld_record_cache (
                query_key TEXT NOT NULL,
                day TEXT NOT NULL,
                hour INTEGER NOT NULL,
                submarket TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (query_key, day, hour, submarket),
                FOREIGN KEY (query_key) REFERENCES pld_query_cache(query_key) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_pld_record_cache_query_key
                ON pld_record_cache(query_key);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_options.ResolveDatabasePath()}");
    }
}

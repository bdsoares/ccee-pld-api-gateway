using System.Globalization;
using Microsoft.Data.Sqlite;
using Ccee.PldApp.Application.Abstractions;
using Ccee.PldApp.Domain;
using Ccee.PldApp.Infrastructure.Configuration;

namespace Ccee.PldApp.Infrastructure.Persistence;

/// <summary>
/// SQLite implementation of the PLD cache repository.
/// </summary>
public sealed class SqlitePldCacheRepository : IPldCacheRepository
{
    private readonly PldGatewayOptions _options;

    public SqlitePldCacheRepository(PldGatewayOptions? options = null)
    {
        _options = options ?? new PldGatewayOptions();
    }

    /// <summary>
    /// Reads a cached query and its records from SQLite when present.
    /// </summary>
    public async Task<PldQueryResult?> GetAsync(PldQuery query, CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Normalize();
        var cacheKey = normalizedQuery.ToCacheKey();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText =
            """
            SELECT used_month_reference_fallback, retrieved_at_utc
            FROM pld_query_cache
            WHERE query_key = $queryKey
            LIMIT 1;
            """;
        metadataCommand.Parameters.AddWithValue("$queryKey", cacheKey);

        await using var metadataReader = await metadataCommand.ExecuteReaderAsync(cancellationToken);
        if (!await metadataReader.ReadAsync(cancellationToken))
            return null;

        var usedMonthReferenceFallback = metadataReader.GetInt32(0) == 1;
        var retrievedAtUtc = DateTimeOffset.Parse(
            metadataReader.GetString(1),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        await using var recordsCommand = connection.CreateCommand();
        recordsCommand.CommandText =
            """
            SELECT day, hour, submarket, value
            FROM pld_record_cache
            WHERE query_key = $queryKey
            ORDER BY day DESC, hour DESC, submarket ASC;
            """;
        recordsCommand.Parameters.AddWithValue("$queryKey", cacheKey);

        await using var recordsReader = await recordsCommand.ExecuteReaderAsync(cancellationToken);
        var records = new List<PldRecord>();

        while (await recordsReader.ReadAsync(cancellationToken))
        {
            records.Add(new PldRecord
            {
                Dia = DateOnly.Parse(recordsReader.GetString(0), CultureInfo.InvariantCulture),
                Hora = recordsReader.GetInt32(1),
                Submercado = recordsReader.GetString(2),
                Valor = decimal.Parse(recordsReader.GetString(3), CultureInfo.InvariantCulture)
            });
        }

        if (records.Count == 0)
            return null;

        return new PldQueryResult
        {
            Query = normalizedQuery,
            Records = records,
            Source = PldQuerySource.Cache,
            RetrievedAtUtc = retrievedAtUtc,
            UsedMonthReferenceFallback = usedMonthReferenceFallback
        };
    }

    /// <summary>
    /// Replaces the cached payload for the given query with the latest retrieved records.
    /// </summary>
    public async Task SaveAsync(PldQueryResult result, CancellationToken cancellationToken = default)
    {
        var normalizedQuery = result.Query.Normalize();
        var cacheKey = normalizedQuery.ToCacheKey();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // The cache is rewritten per query key so the metadata row and its hourly records always stay in sync.
        await using (var metadataCommand = connection.CreateCommand())
        {
            metadataCommand.Transaction = transaction;
            metadataCommand.CommandText =
                """
                INSERT INTO pld_query_cache (
                    query_key,
                    resource_id,
                    day,
                    submarket,
                    limit_value,
                    used_month_reference_fallback,
                    retrieved_at_utc
                )
                VALUES (
                    $queryKey,
                    $resourceId,
                    $day,
                    $submarket,
                    $limitValue,
                    $usedMonthReferenceFallback,
                    $retrievedAtUtc
                )
                ON CONFLICT(query_key) DO UPDATE SET
                    resource_id = excluded.resource_id,
                    day = excluded.day,
                    submarket = excluded.submarket,
                    limit_value = excluded.limit_value,
                    used_month_reference_fallback = excluded.used_month_reference_fallback,
                    retrieved_at_utc = excluded.retrieved_at_utc;
                """;
            metadataCommand.Parameters.AddWithValue("$queryKey", cacheKey);
            metadataCommand.Parameters.AddWithValue("$resourceId", normalizedQuery.ResourceId);
            metadataCommand.Parameters.AddWithValue(
                "$day",
                normalizedQuery.Dia is null
                    ? DBNull.Value
                    : normalizedQuery.Dia.Value.ToString("yyyy-MM-dd"));
            metadataCommand.Parameters.AddWithValue("$submarket", (object?)normalizedQuery.Submercado ?? DBNull.Value);
            metadataCommand.Parameters.AddWithValue("$limitValue", normalizedQuery.Limit);
            metadataCommand.Parameters.AddWithValue("$usedMonthReferenceFallback", result.UsedMonthReferenceFallback ? 1 : 0);
            metadataCommand.Parameters.AddWithValue("$retrievedAtUtc", result.RetrievedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await metadataCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteRecordsCommand = connection.CreateCommand())
        {
            deleteRecordsCommand.Transaction = transaction;
            deleteRecordsCommand.CommandText = "DELETE FROM pld_record_cache WHERE query_key = $queryKey;";
            deleteRecordsCommand.Parameters.AddWithValue("$queryKey", cacheKey);
            await deleteRecordsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var record in result.Records)
        {
            await using var insertRecordCommand = connection.CreateCommand();
            insertRecordCommand.Transaction = transaction;
            insertRecordCommand.CommandText =
                """
                INSERT INTO pld_record_cache (query_key, day, hour, submarket, value)
                VALUES ($queryKey, $day, $hour, $submarket, $value);
                """;
            insertRecordCommand.Parameters.AddWithValue("$queryKey", cacheKey);
            insertRecordCommand.Parameters.AddWithValue("$day", record.Dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertRecordCommand.Parameters.AddWithValue("$hour", record.Hora);
            insertRecordCommand.Parameters.AddWithValue("$submarket", record.Submercado);
            insertRecordCommand.Parameters.AddWithValue("$value", record.Valor.ToString(CultureInfo.InvariantCulture));
            await insertRecordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_options.ResolveDatabasePath()}");
    }
}

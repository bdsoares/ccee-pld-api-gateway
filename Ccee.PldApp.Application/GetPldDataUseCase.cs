using Ccee.PldApp.Application.Abstractions;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Application;

/// <summary>
/// Orchestrates the gateway flow: cache lookup, upstream fetch, fallback query and persistence.
/// </summary>
public sealed class GetPldDataUseCase
{
    private readonly ICceePldSource _source;
    private readonly IPldCacheRepository _cacheRepository;

    public GetPldDataUseCase(ICceePldSource source, IPldCacheRepository cacheRepository)
    {
        _source = source;
        _cacheRepository = cacheRepository;
    }

    /// <summary>
    /// Resolves a query by checking the cache first and falling back to CCEE when necessary.
    /// </summary>
    public async Task<PldQueryResult> ExecuteAsync(
        PldQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Normalize();

        if (normalizedQuery.Limit <= 0 || normalizedQuery.Limit > PldQuery.MaxLimit)
            throw new ArgumentOutOfRangeException(
                nameof(query),
                $"O limite da consulta deve estar entre 1 e {PldQuery.MaxLimit}.");

        var cachedResult = await _cacheRepository.GetAsync(normalizedQuery, cancellationToken);
        if (cachedResult is not null)
        {
            return cachedResult with
            {
                Query = normalizedQuery,
                Source = PldQuerySource.Cache
            };
        }

        var records = await _source.GetAsync(normalizedQuery, useMonthReferenceDate: false, cancellationToken);
        var usedSecondaryDateFallback = false;

        if (records.Count == 0 && normalizedQuery.Dia.HasValue)
        {
            // Compatibility fallback for datasets that still expect DIA as a full date string.
            records = await _source.GetAsync(normalizedQuery, useMonthReferenceDate: true, cancellationToken);
            usedSecondaryDateFallback = records.Count > 0;
        }

        if (records.Count == 0)
            throw new PldQueryNotFoundException(normalizedQuery);

        var result = new PldQueryResult
        {
            Query = normalizedQuery,
            Records = records,
            Source = PldQuerySource.Ccee,
            RetrievedAtUtc = DateTimeOffset.UtcNow,
            // Legacy field name kept for backward compatibility with cache schema/clients.
            UsedMonthReferenceFallback = usedSecondaryDateFallback
        };

        await _cacheRepository.SaveAsync(result, cancellationToken);
        return result;
    }
}

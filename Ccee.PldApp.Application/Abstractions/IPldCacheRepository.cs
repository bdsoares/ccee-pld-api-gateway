using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Application.Abstractions;

/// <summary>
/// Contract for the persistent local cache used by the gateway.
/// </summary>
public interface IPldCacheRepository
{
    /// <summary>
    /// Returns a cached result for the specified query when one exists.
    /// </summary>
    Task<PldQueryResult?> GetAsync(PldQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied result so equivalent queries can be served locally.
    /// </summary>
    Task SaveAsync(PldQueryResult result, CancellationToken cancellationToken = default);
}

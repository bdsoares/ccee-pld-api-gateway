using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Application.Abstractions;

/// <summary>
/// Contract for the upstream PLD source, typically the CCEE open data endpoint.
/// </summary>
public interface ICceePldSource
{
    Task<IReadOnlyList<PldRecord>> GetAsync(PldQuery query, bool useMonthReferenceDate, CancellationToken cancellationToken = default);
}

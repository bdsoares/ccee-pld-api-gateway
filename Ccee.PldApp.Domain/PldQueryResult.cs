namespace Ccee.PldApp.Domain;

/// <summary>
/// Result returned by the gateway after resolving a query from cache or CCEE.
/// </summary>
public sealed record PldQueryResult
{
    /// <summary>
    /// Normalized query that produced this result.
    /// </summary>
    public required PldQuery Query { get; init; }

    /// <summary>
    /// Ordered PLD records returned by the gateway.
    /// </summary>
    public required IReadOnlyList<PldRecord> Records { get; init; }

    /// <summary>
    /// Indicates whether the data came from the local cache or directly from CCEE.
    /// </summary>
    public required PldQuerySource Source { get; init; }

    /// <summary>
    /// UTC timestamp of when the payload was originally obtained.
    /// </summary>
    public required DateTimeOffset RetrievedAtUtc { get; init; }

    /// <summary>
    /// Legacy flag indicating that the secondary date-filter strategy was required.
    /// </summary>
    public required bool UsedMonthReferenceFallback { get; init; }

    /// <summary>
    /// Convenience count for clients that only need the total number of rows.
    /// </summary>
    public int TotalRecords => Records.Count;
}

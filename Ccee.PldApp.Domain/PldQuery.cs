namespace Ccee.PldApp.Domain;

/// <summary>
/// Input used to search PLD records either in the local cache or in CCEE.
/// </summary>
public sealed record PldQuery
{
    public const string DefaultResourceId = "3f279d6b-1069-42f7-9b0a-217b084729c4";
    public const int DefaultLimit = 1000;
    public const int MaxLimit = 10_000;

    /// <summary>
    /// CCEE dataset identifier.
    /// </summary>
    public string ResourceId { get; init; } = DefaultResourceId;

    /// <summary>
    /// Optional day filter. When omitted, the latest records are returned.
    /// </summary>
    public DateOnly? Dia { get; init; }

    /// <summary>
    /// Optional submarket filter.
    /// </summary>
    public string? Submercado { get; init; }

    /// <summary>
    /// Maximum number of records requested from the upstream dataset.
    /// </summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>
    /// Normalizes values that are safe to canonicalize without hiding invalid input.
    /// </summary>
    public PldQuery Normalize()
    {
        return this with
        {
            ResourceId = string.IsNullOrWhiteSpace(ResourceId) ? DefaultResourceId : ResourceId.Trim(),
            Submercado = string.IsNullOrWhiteSpace(Submercado) ? null : Submercado.Trim()
        };
    }

    /// <summary>
    /// Builds a stable cache key so the same logical query always maps to the same row in SQLite.
    /// </summary>
    public string ToCacheKey()
    {
        var normalized = Normalize();
        var dia = normalized.Dia?.ToString("yyyy-MM-dd") ?? "latest";
        var submercado = normalized.Submercado ?? "all";
        return $"{normalized.ResourceId}|{dia}|{submercado}|{normalized.Limit}";
    }

}

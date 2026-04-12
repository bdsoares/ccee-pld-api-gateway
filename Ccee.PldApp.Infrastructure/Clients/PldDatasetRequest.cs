using System.Text;
using System.Text.Json;
using System.Globalization;
using Ccee.PldApp.Domain;

namespace Ccee.PldApp.Infrastructure.Clients;

/// <summary>
/// Internal representation of the exact query sent to the CCEE dataset API.
/// </summary>
internal sealed class PldDatasetRequest
{
    public required string ResourceId { get; init; }
    public DateOnly? Dia { get; init; }
    public bool UseMonthReferenceDate { get; init; }
    public string? Submercado { get; init; }
    public int Limit { get; init; } = 1000;
    public string Sort { get; init; } = "DIA desc,HORA desc";

    /// <summary>
    /// Converts a domain query into the upstream format expected by CCEE.
    /// </summary>
    public static PldDatasetRequest FromQuery(PldQuery query, bool useMonthReferenceDate)
    {
        var normalized = query.Normalize();

        return new PldDatasetRequest
        {
            ResourceId = normalized.ResourceId,
            Dia = normalized.Dia,
            UseMonthReferenceDate = useMonthReferenceDate,
            Submercado = normalized.Submercado,
            Limit = normalized.Limit
        };
    }

    /// <summary>
    /// Serializes the request into the CCEE datastore_search URL.
    /// </summary>
    public string BuildUrl(string baseUrl)
    {
        var builder = new StringBuilder();
        builder.Append(baseUrl);
        builder.Append("?resource_id=").Append(Uri.EscapeDataString(ResourceId));
        builder.Append("&limit=").Append(Limit);
        builder.Append("&sort=").Append(Uri.EscapeDataString(Sort));

        var filters = BuildFilters();
        if (filters.Count > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filters);
            builder.Append("&filters=").Append(Uri.EscapeDataString(filtersJson));
        }

        return builder.ToString();
    }

    private Dictionary<string, object> BuildFilters()
    {
        var filters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (Dia.HasValue)
        {
            if (UseMonthReferenceDate)
            {
                // Compatibility fallback for datasets that store DIA as a full date string.
                filters["DIA"] = new[]
                {
                    Dia.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    Dia.Value.ToString("d/M/yyyy", CultureInfo.InvariantCulture),
                    Dia.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
            }
            else
            {
                // The current PLD_HORARIO resources store DIA as day-of-month text and rely on MES_REFERENCIA.
                filters["MES_REFERENCIA"] = Dia.Value.ToString("yyyyMM", CultureInfo.InvariantCulture);
                filters["DIA"] = Dia.Value.Day.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (!string.IsNullOrWhiteSpace(Submercado))
            filters["SUBMERCADO"] = Submercado;

        return filters;
    }
}

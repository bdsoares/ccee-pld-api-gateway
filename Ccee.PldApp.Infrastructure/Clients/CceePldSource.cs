using Ccee.PldApp.Application.Abstractions;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Domain;
using Ccee.PldApp.Infrastructure.Configuration;
using Ccee.PldApp.Infrastructure.Parsing;

namespace Ccee.PldApp.Infrastructure.Clients;

/// <summary>
/// Reads PLD data from CCEE using a browser context to bypass HTTP blocking.
/// </summary>
public sealed class CceePldSource : ICceePldSource, IDisposable
{
    private readonly PldGatewayOptions _options;
    private readonly BrowserCceeClient _browserClient;

    public CceePldSource(PldGatewayOptions? options = null)
    {
        _options = options ?? new PldGatewayOptions();
        _browserClient = new BrowserCceeClient(_options);
    }

    /// <summary>
    /// Fetches the requested PLD records from CCEE using a browser context.
    /// Direct HTTP calls are blocked by CCEE, so we always use a headless browser.
    /// </summary>
    public async Task<IReadOnlyList<PldRecord>> GetAsync(
        PldQuery query,
        bool useMonthReferenceDate,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Normalize();
        var shouldTryZeroPaddedFallback =
            !useMonthReferenceDate &&
            normalized.Dia.HasValue &&
            normalized.Dia.Value.Day < 10;

        async Task<IReadOnlyList<PldRecord>> FetchAsync(bool useZeroPaddedDay)
        {
            var request = PldDatasetRequest.FromQuery(
                normalized,
                useMonthReferenceDate,
                useZeroPaddedDay);

            var root = await _browserClient.GetRawAsync(request, cancellationToken);
            return PldRecordParser.ParseRecords(root);
        }

        try
        {
            var records = await FetchAsync(useZeroPaddedDay: false);

            if (!shouldTryZeroPaddedFallback || records.Count > 0)
                return records;
        }
        catch (CceeGatewayException) when (shouldTryZeroPaddedFallback)
        {
            // For some days CCEE accepts DIA only with zero-padding (e.g., 09).
            // If the first request fails, retry once with zero-padded DIA.
        }

        return await FetchAsync(useZeroPaddedDay: true);
    }

    public void Dispose()
    {
        // BrowserCceeClient manages the browser context lifecycle internally and does not
        // hold persistent resources that need cleanup. This method is here
        // for future extensibility and to honor the IDisposable contract.
    }
}

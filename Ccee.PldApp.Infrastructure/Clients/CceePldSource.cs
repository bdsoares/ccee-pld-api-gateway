using Ccee.PldApp.Application.Abstractions;
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
        var request = PldDatasetRequest.FromQuery(query, useMonthReferenceDate);
        var root = await _browserClient.GetRawAsync(request, cancellationToken);
        return PldRecordParser.ParseRecords(root);
    }

    public void Dispose()
    {
        // BrowserCceeClient manages the browser context lifecycle internally and does not
        // hold persistent resources that need cleanup. This method is here
        // for future extensibility and to honor the IDisposable contract.
    }
}

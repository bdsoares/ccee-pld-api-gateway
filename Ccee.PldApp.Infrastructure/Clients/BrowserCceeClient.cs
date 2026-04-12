using System.Text.Json;
using PuppeteerSharp;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Infrastructure.Configuration;

namespace Ccee.PldApp.Infrastructure.Clients;

/// <summary>
/// Uses a real browser context to read data from CCEE.
/// </summary>
internal sealed class BrowserCceeClient
{
    private static readonly Dictionary<string, string> DefaultHeaders = new()
    {
        ["Accept"] = "application/json, text/plain, */*",
        ["Accept-Language"] = "pt-BR,pt;q=0.9,en-US;q=0.8"
    };
    private static readonly SemaphoreSlim BrowserSetupLock = new(1, 1);
    private static bool _browserReady;

    private readonly PldGatewayOptions _options;

    public BrowserCceeClient(PldGatewayOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Executes the upstream request inside a browser context and returns the JSON payload.
    /// </summary>
    public async Task<JsonElement> GetRawAsync(PldDatasetRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAvailableAsync(cancellationToken);
        var attempts = Math.Max(1, _options.HttpMaxAttempts);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var launchOptions = new LaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
            };

            var executablePath = ResolveBrowserExecutablePath();
            if (!string.IsNullOrWhiteSpace(executablePath))
                launchOptions.ExecutablePath = executablePath;

            var requestUrl = request.BuildUrl(_options.BaseUrl);

            try
            {
                var browser = await Puppeteer.LaunchAsync(launchOptions);
                try
                {
                    var page = await browser.NewPageAsync();
                    await page.SetUserAgentAsync(_options.UserAgent);
                    await page.SetExtraHttpHeadersAsync(DefaultHeaders);
                    return await FetchJsonAsync(page, requestUrl, _options.HttpTimeoutSeconds);
                }
                finally
                {
                    await browser.CloseAsync();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                lastException = ex;
                var retryDelay = CalculateRetryDelay(attempt);
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw new CceeGatewayException(
            $"Falha ao consultar a CCEE via browser apos {attempts} tentativa(s).",
            lastException ?? new Exception("Falha desconhecida no cliente de browser."));
    }

    private async Task EnsureBrowserAvailableAsync(CancellationToken cancellationToken)
    {
        if (_browserReady)
            return;

        await BrowserSetupLock.WaitAsync(cancellationToken);

        try
        {
            if (_browserReady)
                return;

            if (!string.IsNullOrWhiteSpace(ResolveBrowserExecutablePath()))
            {
                _browserReady = true;
                return;
            }

            if (!_options.DownloadBrowserIfMissing)
            {
                throw new CceeGatewayException("Nenhum executavel de Chromium foi configurado e DownloadBrowserIfMissing=false.");
            }

            // The browser download only happens once when no executable was explicitly configured.
            await new BrowserFetcher().DownloadAsync();
            _browserReady = true;
        }
        finally
        {
            BrowserSetupLock.Release();
        }
    }

    private string? ResolveBrowserExecutablePath()
    {
        if (string.IsNullOrWhiteSpace(_options.BrowserExecutablePath))
            return null;

        if (Path.IsPathRooted(_options.BrowserExecutablePath))
            return File.Exists(_options.BrowserExecutablePath) ? _options.BrowserExecutablePath : null;

        var resolvedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.BrowserExecutablePath));
        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    private static async Task<JsonElement> FetchJsonAsync(IPage page, string url, int timeoutSeconds)
    {
        var timeoutMilliseconds = Math.Max(1, timeoutSeconds) * 1000;
        var response = await page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.Networkidle2 },
            Timeout = timeoutMilliseconds
        });
        var content = await (response?.TextAsync() ?? Task.FromResult(string.Empty));

        if (TryParseJson(content, out var element) && IsSuccessResponse(element))
            return element;

        // If navigation does not expose the JSON body correctly, fetch it again inside the browser page context.
        var fetchContent = await page.EvaluateFunctionAsync<string>(
            """
            async (url, timeoutMs) => {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), timeoutMs);
                try {
                    const response = await fetch(url, {
                        headers: { "Accept": "application/json, text/plain, */*" },
                        signal: controller.signal
                    });
                    return await response.text();
                } finally {
                    clearTimeout(timer);
                }
            }
            """,
            url,
            timeoutMilliseconds);

        if (TryParseJson(fetchContent, out var fetchElement) && IsSuccessResponse(fetchElement))
            return fetchElement;

        throw new CceeGatewayException($"Falha ao obter JSON valido da CCEE via browser. Conteudo retornado: {Truncate(content, 400)}");
    }

    private static bool TryParseJson(string source, out JsonElement element)
    {
        element = default;

        if (string.IsNullOrWhiteSpace(source))
            return false;

        try
        {
            using var document = JsonDocument.Parse(source);
            element = document.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSuccessResponse(JsonElement element)
    {
        return element.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static TimeSpan CalculateRetryDelay(int attempt)
    {
        var delayMilliseconds = Math.Min(2000, 250 * (int)Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }
}

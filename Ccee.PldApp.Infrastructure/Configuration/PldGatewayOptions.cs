namespace Ccee.PldApp.Infrastructure.Configuration;

/// <summary>
/// Runtime settings for the PLD gateway.
/// </summary>
public sealed class PldGatewayOptions
{
    public const string SectionName = "PldGateway";

    /// <summary>
    /// Base CCEE endpoint used by the gateway.
    /// </summary>
    public string BaseUrl { get; set; } = "https://dadosabertos.ccee.org.br/api/3/action/datastore_search";

    /// <summary>
    /// Relative or absolute path to the SQLite database.
    /// </summary>
    public string DatabasePath { get; set; } = Path.Combine("data", "pld-cache.db");

    /// <summary>
    /// User-Agent used for upstream requests.
    /// </summary>
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36";

    /// <summary>
    /// Timeout, in seconds, for browser navigation and fetch operations against CCEE.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts before the browser client returns failure.
    /// </summary>
    public int HttpMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Allows PuppeteerSharp to download a local browser automatically when needed.
    /// </summary>
    public bool DownloadBrowserIfMissing { get; set; } = false;

    /// <summary>
    /// Path to an existing Chromium or Chrome executable.
    /// </summary>
    public string? BrowserExecutablePath { get; set; }

    /// <summary>
    /// Enables automatic lookup of missing yearly PLD_HORARIO resource IDs.
    /// </summary>
    public bool EnableResourceIdAutoDiscovery { get; set; } = true;

    /// <summary>
    /// CKAN package endpoint used to discover PLD_HORARIO resources by year.
    /// </summary>
    public string ResourceCatalogUrl { get; set; } = "https://dadosabertos.ccee.org.br/api/3/action/package_show?id=pld_horario";

    /// <summary>
    /// Resource IDs indexed by reference year (e.g. 2026 -> "resource-id").
    /// </summary>
    public Dictionary<int, string> ResourceIdsByYear { get; set; } = new();

    /// <summary>
    /// Resolves the database path relative to the application base directory when a relative path is provided.
    /// </summary>
    public string ResolveDatabasePath()
    {
        if (Path.IsPathRooted(DatabasePath))
            return DatabasePath;

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DatabasePath));
    }

    /// <summary>
    /// Resolves the dataset resource ID for the requested date year.
    /// </summary>
    public bool TryResolveResourceId(DateOnly? date, out string resourceId, out string errorMessage)
    {
        var targetYear = date?.Year ?? DateTime.Now.Year;

        if (ResourceIdsByYear.TryGetValue(targetYear, out var configuredResourceId)
            && !string.IsNullOrWhiteSpace(configuredResourceId))
        {
            resourceId = configuredResourceId.Trim();
            errorMessage = string.Empty;
            return true;
        }

        var configuredYears = ResourceIdsByYear.Keys.OrderBy(year => year).ToArray();
        resourceId = string.Empty;
        errorMessage = configuredYears.Length == 0
            ? "Nenhum resource_id foi configurado em PldGateway:ResourceIdsByYear."
            : $"Nao existe resource_id configurado para o ano {targetYear}. Anos disponiveis: {string.Join(", ", configuredYears)}.";
        return false;
    }

    /// <summary>
    /// Attempts to locate a Chrome or Chromium executable in common system locations.
    /// Returns the path if found, or null if not found.
    /// </summary>
    public string? AutoDetectBrowserPath()
    {
        // Common Chrome/Chromium installation paths on Windows
        var commonPaths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Chromium\Application\chrome.exe"
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}

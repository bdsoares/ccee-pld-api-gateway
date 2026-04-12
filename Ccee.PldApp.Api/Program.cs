using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Ccee.PldApp.Application;
using Ccee.PldApp.Application.Abstractions;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Domain;
using Ccee.PldApp.Infrastructure.Clients;
using Ccee.PldApp.Infrastructure.Configuration;
using Ccee.PldApp.Infrastructure.Persistence;
using Serilog;

const string DashboardAuthCookieName = "pld_dashboard_session";
var allowedSubmercados = new HashSet<string>(StringComparer.Ordinal)
{
    "SUDESTE",
    "SUL",
    "NORDESTE",
    "NORTE"
};

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("Configuration/SecurityConfig.json", optional: true, reloadOnChange: true);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "Ccee.PldApp.Api");
});

var gatewayOptions = LoadGatewayOptions(builder.Configuration);
if (string.IsNullOrWhiteSpace(gatewayOptions.BrowserExecutablePath))
{
    var foundPath = gatewayOptions.AutoDetectBrowserPath();
    if (!string.IsNullOrWhiteSpace(foundPath))
        gatewayOptions.BrowserExecutablePath = foundPath;
}

var dashboardAuthOptions = LoadDashboardAuthOptions(builder.Configuration);
var uiOptions = LoadUiOptions(builder.Configuration);
var loginRequestJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

builder.Services.AddSingleton(gatewayOptions);
builder.Services.AddSingleton(dashboardAuthOptions);
builder.Services.AddSingleton(uiOptions);
builder.Services.AddSingleton(new ConcurrentDictionary<string, DashboardSession>(StringComparer.Ordinal));
builder.Services.AddSingleton(new ConcurrentDictionary<int, string>());
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<IPldCacheRepository, SqlitePldCacheRepository>();
builder.Services.AddSingleton<ICceePldSource, CceePldSource>();
builder.Services.AddSingleton<GetPldDataUseCase>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<JsonOptions>(opts =>
{
    opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUIClients", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' cdn.jsdelivr.net; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseCors("AllowUIClients");
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/dashboard.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/dashboard");
        return;
    }

    if (context.Request.Path.Equals("/login.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/login");
        return;
    }

    await next();
});
app.UseStaticFiles();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Aplicacao iniciando...");

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "CCEE PLD Gateway API v1");
    c.RoutePrefix = "swagger";
});

if (!string.IsNullOrWhiteSpace(gatewayOptions.BrowserExecutablePath) && File.Exists(gatewayOptions.BrowserExecutablePath))
{
    logger.LogInformation("Browser encontrado em: {BrowserExecutablePath}", gatewayOptions.BrowserExecutablePath);
}
else if (!gatewayOptions.DownloadBrowserIfMissing)
{
    logger.LogCritical("Erro critico: nenhum browser configurado e DownloadBrowserIfMissing=false.");
    logger.LogCritical("Configure PldGateway:BrowserExecutablePath, instale Chrome/Chromium, ou habilite DownloadBrowserIfMissing.");
    Environment.Exit(1);
}
else
{
    logger.LogWarning("Nenhum browser configurado. DownloadBrowserIfMissing=true e o download ocorrera sob demanda.");
}

if (dashboardAuthOptions.RequireAuthentication && dashboardAuthOptions.Users.Count == 0)
{
    logger.LogCritical("Erro critico: autenticacao do dashboard habilitada sem usuarios configurados.");
    Environment.Exit(1);
}

if (gatewayOptions.ResourceIdsByYear.Count == 0 && !gatewayOptions.EnableResourceIdAutoDiscovery)
{
    logger.LogCritical("Erro critico: nenhum resource_id foi configurado em PldGateway:ResourceIdsByYear.");
    logger.LogCritical("Ative PldGateway:EnableResourceIdAutoDiscovery=true ou configure os anos manualmente.");
    Environment.Exit(1);
}
else if (gatewayOptions.ResourceIdsByYear.Count == 0)
{
    logger.LogWarning("Nenhum resource_id configurado. A aplicacao tentara descobrir automaticamente via catalogo da CCEE.");
}

await app.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync();
logger.LogInformation("Banco de dados inicializado");

app.MapGet("/", (PldGatewayOptions options) => Results.Ok(new
{
    service = "ccee-pld-gateway",
    status = "online",
    databasePath = options.ResolveDatabasePath(),
    endpoints = new[]
    {
        "/swagger",
        "/login",
        "/dashboard",
        "/health",
        "/api/pld?date=2026-04-11&submercado=SUL",
        "/api/pld?submercado=SUDESTE&limit=24"
    }
}))
.WithName("GetIndex")
.Produces(StatusCodes.Status200OK);

app.MapGet("/health", (PldGatewayOptions options) => Results.Ok(new
{
    status = "ok",
    databasePath = options.ResolveDatabasePath()
}))
.WithName("GetHealth")
.Produces(StatusCodes.Status200OK);

app.MapGet("/login", (HttpContext httpContext, DashboardAuthOptions authOptions, UiOptions localUiOptions, IWebHostEnvironment environment, ConcurrentDictionary<string, DashboardSession> sessions, ILogger<Program> localLogger) =>
{
    if (!authOptions.RequireAuthentication)
        return Results.Redirect("/dashboard");

    if (TryGetAuthenticatedUser(httpContext, sessions, out _))
        return Results.Redirect("/dashboard");

    return ServeHtml("login.html", localUiOptions, environment, localLogger);
})
.WithName("GetLogin")
.Produces(StatusCodes.Status200OK);

app.MapPost("/api/auth/login", async (HttpContext httpContext, DashboardAuthOptions authOptions, ConcurrentDictionary<string, DashboardSession> sessions, CancellationToken cancellationToken) =>
{
    if (!authOptions.RequireAuthentication)
        return Results.Ok(new { message = "Autenticacao do dashboard esta desabilitada." });

    LoginRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<LoginRequest>(
            httpContext.Request.Body,
            loginRequestJsonOptions,
            cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { message = "Payload de login invalido." });
    }

    if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Usuario e senha sao obrigatorios." });

    CleanupExpiredSessions(sessions);

    var user = authOptions.Users.FirstOrDefault(candidate =>
        string.Equals(candidate.Username, request.Username, StringComparison.Ordinal) &&
        string.Equals(candidate.Password, request.Password, StringComparison.Ordinal));

    if (user is null)
        return Results.Unauthorized();

    var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, authOptions.SessionTimeoutMinutes));
    var token = CreateSessionToken();
    sessions[token] = new DashboardSession(user.Username, expiresAtUtc);

    httpContext.Response.Cookies.Append(DashboardAuthCookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Expires = expiresAtUtc.UtcDateTime,
        Path = "/"
    });

    return Results.Ok(new
    {
        username = user.Username,
        expiresAtUtc
    });
})
.WithName("PostLogin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

app.MapPost("/api/auth/logout", (HttpContext httpContext, ConcurrentDictionary<string, DashboardSession> sessions) =>
{
    if (httpContext.Request.Cookies.TryGetValue(DashboardAuthCookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        sessions.TryRemove(token, out _);

    httpContext.Response.Cookies.Delete(DashboardAuthCookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/"
    });

    return Results.Ok(new { success = true });
})
.WithName("PostLogout")
.Produces(StatusCodes.Status200OK);

app.MapGet("/api/auth/me", (HttpContext httpContext, DashboardAuthOptions authOptions, ConcurrentDictionary<string, DashboardSession> sessions) =>
{
    if (!authOptions.RequireAuthentication)
        return Results.Ok(new { authenticated = false, authenticationDisabled = true });

    if (!TryGetAuthenticatedUser(httpContext, sessions, out var username))
        return Results.Unauthorized();

    return Results.Ok(new { authenticated = true, username });
})
.WithName("GetAuthMe")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/dashboard", (HttpContext httpContext, DashboardAuthOptions authOptions, UiOptions localUiOptions, IWebHostEnvironment environment, ConcurrentDictionary<string, DashboardSession> sessions, ILogger<Program> localLogger) =>
{
    if (authOptions.RequireAuthentication && !TryGetAuthenticatedUser(httpContext, sessions, out _))
        return Results.Redirect("/login");

    return ServeHtml("dashboard.html", localUiOptions, environment, localLogger);
})
.WithName("GetDashboard")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status302Found)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/api/pld", async (
    HttpContext httpContext,
    GetPldDataUseCase useCase,
    PldGatewayOptions options,
    ConcurrentDictionary<int, string> discoveredResourceIds,
    IWebHostEnvironment environment,
    ILogger<Program> localLogger,
    CancellationToken cancellationToken) =>
{
    if (!TryBuildQuery(httpContext.Request.Query, allowedSubmercados, out var query, out var problem))
    {
        localLogger.LogWarning("Parametros invalidos na requisicao: {Problem}", problem.Detail);
        return Results.BadRequest(problem);
    }

    var resourceResolution = await ResolveResourceIdAsync(
        query.Dia,
        options,
        discoveredResourceIds,
        environment.ContentRootPath,
        localLogger,
        cancellationToken);

    if (!resourceResolution.Success)
    {
        var resolutionProblem = CreateValidationProblem("Configuracao invalida", resourceResolution.ErrorMessage ?? "Falha ao resolver resource_id.");
        localLogger.LogWarning("Falha ao resolver resource_id: {Problem}", resolutionProblem.Detail);
        return Results.BadRequest(resolutionProblem);
    }

    query = query with { ResourceId = resourceResolution.ResourceId! };

    localLogger.LogInformation(
        "Consultando PLD: Dia={Dia}, Submercado={Submercado}, Limit={Limit}, ResourceId={ResourceId}",
        query.Dia?.ToString("yyyy-MM-dd") ?? "todos",
        query.Submercado ?? "todos",
        query.Limit,
        query.ResourceId);

    try
    {
        var result = await useCase.ExecuteAsync(query, cancellationToken);
        return Results.Ok(result);
    }
    catch (PldQueryNotFoundException ex)
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Nenhum dado encontrado",
            Detail = ex.Message,
            Status = StatusCodes.Status404NotFound
        });
    }
    catch (CceeGatewayException ex)
    {
        localLogger.LogError(ex, "Falha ao consultar a CCEE");
        return Results.Problem(
            title: "Falha ao consultar a CCEE",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Parametro invalido",
            Detail = ex.Message,
            Status = StatusCodes.Status400BadRequest
        });
    }
})
.WithName("GetPldData")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status502BadGateway);

app.Run();

static bool TryBuildQuery(
    IQueryCollection queryCollection,
    ISet<string> allowedSubmercados,
    out PldQuery query,
    out ProblemDetails problem)
{
    query = default!;
    problem = null!;

    if (HasAnyQueryKey(queryCollection, "resourceId", "forceRefresh"))
    {
        problem = CreateValidationProblem(
            "Parametro invalido",
            "Os parametros 'resourceId' e 'forceRefresh' nao sao suportados por este endpoint.");
        return false;
    }

    var rawDate = GetStringQueryValue(queryCollection, "date");
    if (!TryParseDate(rawDate, out var dia))
    {
        problem = CreateValidationProblem("Parametro invalido", "O parametro 'date' deve estar no formato YYYY-MM-DD.");
        return false;
    }

    var rawLimit = GetStringQueryValue(queryCollection, "limit");
    if (!TryParseLimit(rawLimit, out var limit))
    {
        problem = CreateValidationProblem("Parametro invalido", $"O parametro 'limit' deve ser um inteiro entre 1 e {PldQuery.MaxLimit}.");
        return false;
    }

    var rawSubmercado = GetStringQueryValue(queryCollection, "submercado");
    if (!TryParseSubmercado(rawSubmercado, allowedSubmercados, out var submercado))
    {
        problem = CreateValidationProblem(
            "Parametro invalido",
            "O parametro 'submercado' deve ser exatamente um dos valores: SUDESTE, SUL, NORDESTE, NORTE.");
        return false;
    }

    query = new PldQuery
    {
        ResourceId = PldQuery.DefaultResourceId,
        Dia = dia,
        Submercado = submercado,
        Limit = limit
    }.Normalize();

    return true;
}

static string? GetStringQueryValue(IQueryCollection queryCollection, params string[] keys)
{
    foreach (var key in keys)
    {
        if (queryCollection.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value.ToString();
    }

    return null;
}

static bool HasAnyQueryKey(IQueryCollection queryCollection, params string[] keys)
{
    foreach (var key in keys)
    {
        if (queryCollection.ContainsKey(key))
            return true;
    }

    return false;
}

static async Task<ResourceIdResolution> ResolveResourceIdAsync(
    DateOnly? date,
    PldGatewayOptions options,
    ConcurrentDictionary<int, string> discoveredResourceIds,
    string contentRootPath,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    if (options.TryResolveResourceId(date, out var configuredResourceId, out _))
        return ResourceIdResolution.FromSuccess(configuredResourceId, autoDiscovered: false);

    var targetYear = date?.Year ?? DateTime.Now.Year;

    if (discoveredResourceIds.TryGetValue(targetYear, out var cachedResourceId))
        return ResourceIdResolution.FromSuccess(cachedResourceId, autoDiscovered: true);

    if (!options.EnableResourceIdAutoDiscovery)
    {
        options.TryResolveResourceId(date, out _, out var disabledErrorMessage);
        return ResourceIdResolution.FromFailure(disabledErrorMessage);
    }

    var discoveredResourceId = await DiscoverResourceIdForYearAsync(targetYear, options, cancellationToken);
    if (string.IsNullOrWhiteSpace(discoveredResourceId))
    {
        options.TryResolveResourceId(date, out _, out var baseErrorMessage);
        return ResourceIdResolution.FromFailure(
            $"{baseErrorMessage} A descoberta automatica tambem nao encontrou o recurso para o ano {targetYear}.");
    }

    discoveredResourceIds[targetYear] = discoveredResourceId;
    options.ResourceIdsByYear[targetYear] = discoveredResourceId;

    var persisted = await TryUpsertResourceIdInAppSettingsAsync(
        contentRootPath,
        targetYear,
        discoveredResourceId,
        logger,
        cancellationToken);

    if (persisted)
    {
        logger.LogWarning(
            "resource_id do ano {Year} descoberto automaticamente: {ResourceId}. Valor salvo em appsettings.json.",
            targetYear,
            discoveredResourceId);
    }
    else
    {
        logger.LogWarning(
            "resource_id do ano {Year} descoberto automaticamente: {ResourceId}, mas nao foi possivel salvar em appsettings.json.",
            targetYear,
            discoveredResourceId);
    }

    return ResourceIdResolution.FromSuccess(discoveredResourceId, autoDiscovered: true);
}

static async Task<string?> DiscoverResourceIdForYearAsync(int year, PldGatewayOptions options, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(options.ResourceCatalogUrl))
        return null;

    try
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.HttpTimeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);

        using var response = await client.GetAsync(options.ResourceCatalogUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return null;

        if (!root.TryGetProperty("result", out var result))
            return null;

        if (!result.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            return null;

        var expectedName = $"pld_horario_{year}";
        foreach (var resource in resources.EnumerateArray())
        {
            var resourceName = GetJsonStringProperty(resource, "name");
            if (!string.Equals(resourceName, expectedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var resourceId = GetJsonStringProperty(resource, "id");
            if (!string.IsNullOrWhiteSpace(resourceId))
                return resourceId.Trim();
        }
    }
    catch
    {
        // Ignore discovery errors and keep the normal configured flow.
    }

    return null;
}

static string? GetJsonStringProperty(JsonElement element, string propertyName)
{
    if (element.TryGetProperty(propertyName, out var directProperty))
        return directProperty.ValueKind == JsonValueKind.String ? directProperty.GetString() : directProperty.ToString();

    foreach (var property in element.EnumerateObject())
    {
        if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            continue;

        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
    }

    return null;
}

static async Task<bool> TryUpsertResourceIdInAppSettingsAsync(
    string contentRootPath,
    int year,
    string resourceId,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    var appsettingsPath = Path.Combine(contentRootPath, "appsettings.json");
    if (!File.Exists(appsettingsPath))
    {
        logger.LogWarning("Arquivo appsettings.json nao encontrado para persistir resource_id: {Path}", appsettingsPath);
        return false;
    }

    await ResourceIdAppSettingsSync.Semaphore.WaitAsync(cancellationToken);

    try
    {
        var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
        var rootNode = JsonNode.Parse(json) as JsonObject;
        if (rootNode is null)
        {
            logger.LogWarning("Nao foi possivel interpretar appsettings.json como objeto JSON: {Path}", appsettingsPath);
            return false;
        }

        var pldGatewayNode = rootNode["PldGateway"] as JsonObject ?? new JsonObject();
        rootNode["PldGateway"] = pldGatewayNode;

        var resourceIdsNode = pldGatewayNode["ResourceIdsByYear"] as JsonObject ?? new JsonObject();
        pldGatewayNode["ResourceIdsByYear"] = resourceIdsNode;

        var yearKey = year.ToString(CultureInfo.InvariantCulture);
        var currentValue = resourceIdsNode[yearKey]?.GetValue<string>();
        if (string.Equals(currentValue, resourceId, StringComparison.Ordinal))
            return true;

        resourceIdsNode[yearKey] = resourceId;

        var updatedJson = rootNode.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(appsettingsPath, updatedJson + Environment.NewLine, cancellationToken);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Falha ao persistir resource_id descoberto automaticamente em appsettings.json.");
        return false;
    }
    finally
    {
        ResourceIdAppSettingsSync.Semaphore.Release();
    }
}

static bool TryParseDate(string? rawValue, out DateOnly? value)
{
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        value = null;
        return true;
    }

    if (DateOnly.TryParseExact(rawValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
    {
        value = parsed;
        return true;
    }

    value = null;
    return false;
}

static bool TryParseLimit(string? rawValue, out int value)
{
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        value = PldQuery.DefaultLimit;
        return true;
    }

    return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
           && value > 0
           && value <= PldQuery.MaxLimit;
}

static bool TryParseSubmercado(string? rawValue, ISet<string> allowedSubmercados, out string? value)
{
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        value = null;
        return true;
    }

    var candidate = rawValue.Trim();
    if (allowedSubmercados.Contains(candidate))
    {
        value = candidate;
        return true;
    }

    value = null;
    return false;
}

static IResult ServeHtml(
    string fileName,
    UiOptions uiOptions,
    IWebHostEnvironment environment,
    Microsoft.Extensions.Logging.ILogger logger)
{
    var configuredPagesRoot = Path.IsPathRooted(uiOptions.PagesPath)
        ? uiOptions.PagesPath
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, uiOptions.PagesPath));

    var configuredHtmlPath = Path.Combine(configuredPagesRoot, fileName);
    if (File.Exists(configuredHtmlPath))
    {
        var configuredHtml = File.ReadAllText(configuredHtmlPath);
        return Results.Content(configuredHtml, "text/html");
    }

    var webRootPath = string.IsNullOrWhiteSpace(environment.WebRootPath)
        ? null
        : Path.Combine(environment.WebRootPath, fileName);

    if (!string.IsNullOrWhiteSpace(webRootPath) && File.Exists(webRootPath))
    {
        var webRootHtml = File.ReadAllText(webRootPath);
        return Results.Content(webRootHtml, "text/html");
    }

    logger.LogWarning(
        "Arquivo HTML nao encontrado. Configurado: {ConfiguredPath}. Fallback webroot: {WebRootPath}",
        configuredHtmlPath,
        webRootPath ?? "(indisponivel)");

    return Results.NotFound(new { error = "Pagina indisponivel" });
}

static bool TryGetAuthenticatedUser(
    HttpContext httpContext,
    ConcurrentDictionary<string, DashboardSession> sessions,
    out string username)
{
    username = string.Empty;

    CleanupExpiredSessions(sessions);

    if (!httpContext.Request.Cookies.TryGetValue(DashboardAuthCookieName, out var token) || string.IsNullOrWhiteSpace(token))
        return false;

    if (!sessions.TryGetValue(token, out var session))
        return false;

    if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
    {
        sessions.TryRemove(token, out _);
        return false;
    }

    username = session.Username;
    return true;
}

static void CleanupExpiredSessions(ConcurrentDictionary<string, DashboardSession> sessions)
{
    var now = DateTimeOffset.UtcNow;

    foreach (var entry in sessions)
    {
        if (entry.Value.ExpiresAtUtc <= now)
            sessions.TryRemove(entry.Key, out _);
    }
}

static string CreateSessionToken()
{
    var bytes = RandomNumberGenerator.GetBytes(48);
    return Convert.ToBase64String(bytes)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');
}

static PldGatewayOptions LoadGatewayOptions(IConfiguration configuration)
{
    var options = new PldGatewayOptions();

    var databasePath = configuration[$"{PldGatewayOptions.SectionName}:DatabasePath"];
    if (!string.IsNullOrWhiteSpace(databasePath))
        options.DatabasePath = databasePath;

    var baseUrl = configuration[$"{PldGatewayOptions.SectionName}:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
        options.BaseUrl = baseUrl;

    var userAgent = configuration[$"{PldGatewayOptions.SectionName}:UserAgent"];
    if (!string.IsNullOrWhiteSpace(userAgent))
        options.UserAgent = userAgent;

    if (bool.TryParse(configuration[$"{PldGatewayOptions.SectionName}:DownloadBrowserIfMissing"], out var downloadBrowserIfMissing))
        options.DownloadBrowserIfMissing = downloadBrowserIfMissing;

    if (int.TryParse(configuration[$"{PldGatewayOptions.SectionName}:HttpTimeoutSeconds"], out var httpTimeoutSeconds) && httpTimeoutSeconds > 0)
        options.HttpTimeoutSeconds = httpTimeoutSeconds;

    if (int.TryParse(configuration[$"{PldGatewayOptions.SectionName}:HttpMaxAttempts"], out var httpMaxAttempts) && httpMaxAttempts > 0)
        options.HttpMaxAttempts = httpMaxAttempts;

    var browserExecutablePath = configuration[$"{PldGatewayOptions.SectionName}:BrowserExecutablePath"];
    if (!string.IsNullOrWhiteSpace(browserExecutablePath))
        options.BrowserExecutablePath = browserExecutablePath;

    if (bool.TryParse(configuration[$"{PldGatewayOptions.SectionName}:EnableResourceIdAutoDiscovery"], out var enableResourceIdAutoDiscovery))
        options.EnableResourceIdAutoDiscovery = enableResourceIdAutoDiscovery;

    var resourceCatalogUrl = configuration[$"{PldGatewayOptions.SectionName}:ResourceCatalogUrl"];
    if (!string.IsNullOrWhiteSpace(resourceCatalogUrl))
        options.ResourceCatalogUrl = resourceCatalogUrl;

    var resourceIdsByYearSection = configuration.GetSection($"{PldGatewayOptions.SectionName}:ResourceIdsByYear");
    foreach (var child in resourceIdsByYearSection.GetChildren())
    {
        if (!int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            continue;

        if (string.IsNullOrWhiteSpace(child.Value))
            continue;

        options.ResourceIdsByYear[year] = child.Value.Trim();
    }

    return options;
}

static UiOptions LoadUiOptions(IConfiguration configuration)
{
    var options = new UiOptions();
    var pagesPath = configuration["Ui:PagesPath"];

    if (!string.IsNullOrWhiteSpace(pagesPath))
        options.PagesPath = pagesPath.Trim();

    return options;
}

static DashboardAuthOptions LoadDashboardAuthOptions(IConfiguration configuration)
{
    var options = new DashboardAuthOptions();
    var section = configuration.GetSection("Security:Dashboard");

    if (!section.Exists())
        return options;

    if (bool.TryParse(section["RequireAuthentication"], out var requireAuthentication))
        options.RequireAuthentication = requireAuthentication;

    if (int.TryParse(section["SessionTimeoutMinutes"], out var sessionTimeoutMinutes) && sessionTimeoutMinutes > 0)
        options.SessionTimeoutMinutes = sessionTimeoutMinutes;

    var users = section
        .GetSection("Users")
        .GetChildren()
        .Select(child => new DashboardUser
        {
            Username = child["Username"]?.Trim() ?? string.Empty,
            Password = child["Password"] ?? string.Empty
        })
        .Where(user => !string.IsNullOrWhiteSpace(user.Username) && !string.IsNullOrWhiteSpace(user.Password))
        .ToArray();

    if (users.Length > 0)
        options.Users = users;

    return options;
}

static ProblemDetails CreateValidationProblem(string title, string detail)
{
    return new ProblemDetails
    {
        Title = title,
        Detail = detail,
        Status = StatusCodes.Status400BadRequest
    };
}

internal sealed class UiOptions
{
    public string PagesPath { get; set; } = Path.Combine("Configuration", "Ui");
}

internal static class ResourceIdAppSettingsSync
{
    public static readonly SemaphoreSlim Semaphore = new(1, 1);
}

internal sealed record ResourceIdResolution(bool Success, string? ResourceId, string? ErrorMessage, bool AutoDiscovered)
{
    public static ResourceIdResolution FromSuccess(string resourceId, bool autoDiscovered)
        => new(true, resourceId, null, autoDiscovered);

    public static ResourceIdResolution FromFailure(string errorMessage)
        => new(false, null, errorMessage, false);
}

internal sealed class DashboardAuthOptions
{
    public bool RequireAuthentication { get; set; } = true;
    public int SessionTimeoutMinutes { get; set; } = 60;
    public IReadOnlyList<DashboardUser> Users { get; set; } = Array.Empty<DashboardUser>();
}

internal sealed class DashboardUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

internal sealed record DashboardSession(string Username, DateTimeOffset ExpiresAtUtc);
internal sealed record LoginRequest(string? Username, string? Password);
